using Fusion.Common;
using Fusion.Common.Contracts;
using Serilog;

namespace Fusion.Core.Replay;

public enum ReplayState
{
    Idle,
    Playing,
    Completed,
    Stopped,
    Faulted
}

/// <summary>Parameters for one replay run.</summary>
public sealed record ReplayRequest
{
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Inclusive start of the replay window (null = start of file).</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Inclusive end of the replay window (null = end of file).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// Playback speed multiplier: 1 = recorded pace, 5 = 5x, 0 = max (no inter-message delay).
    /// </summary>
    public double Speed { get; init; } = 1.0;
}

/// <summary>Progress snapshot raised while a replay runs.</summary>
public sealed record ReplayProgress(
    ReplayState State,
    DateTimeOffset? Position,
    int Published,
    string? Error = null);

/// <summary>
/// Replays recorded bus traffic from BusAuditLogger audit files.
///
/// This is a plain in-process service (not an element) because the audit log is a local
/// CLEF file written by Serilog under {BaseDirectory}/../logs/audit — it is only reachable
/// by processes sharing the Output folder, so there is no remote endpoint to adapt and
/// nothing for an element manager to manage.
///
/// SAFETY GUARD — replay namespace:
/// Every replayed message is re-published on "REPLAY.{originalTopic}" and NEVER on the
/// original topic. Publishing on original topics would re-trigger live reactions and real
/// hardware (printers, PLCs, host connections). Consumers that want replayed traffic must
/// explicitly subscribe to REPLAY.* topics. Records whose topic already carries the REPLAY
/// prefix are skipped entirely (no replay-of-replay), and BusAuditLogger refuses to audit
/// REPLAY.* topics so replays never pollute the historical record.
/// </summary>
public class ReplayService
{
    /// <summary>The namespace prefix applied to every replayed topic.</summary>
    public const string TopicPrefix = "REPLAY.";

    private const string SourceName = "ReplayService";

    private readonly IMessageBus _bus;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public ReplayProgress Progress { get; private set; } = new(ReplayState.Idle, null, 0);
    public event Action<ReplayProgress>? ProgressChanged;

    /// <summary>
    /// Directory scanned for audit windows. Defaults to the Serilog audit sink location
    /// ({BaseDirectory}/../logs/audit) used by CoreServicesExtensions.SetupLogger.
    /// </summary>
    public string AuditDirectory { get; set; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "audit"));

    /// <param name="bus">Bus that receives the REPLAY.* publications.</param>
    /// <param name="delay">
    /// Inter-message wait strategy. Injectable so tests can use a virtual clock; defaults to Task.Delay.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ReplayService(IMessageBus bus, Func<TimeSpan, CancellationToken, Task>? delay = null, ILogger? logger = null)
    {
        _bus = bus;
        _delay = delay ?? Task.Delay;
        _logger = (logger ?? Log.Logger).ForContext("ElementName", SourceName);
    }

    /// <summary>Lists the audit files available for replay (newest first).</summary>
    public IReadOnlyList<AuditWindow> ListWindows() => AuditLogReader.ListWindows(AuditDirectory);

    public bool IsPlaying => Progress.State == ReplayState.Playing;

    /// <summary>
    /// Starts replaying a file in the background. Any replay already running is stopped first.
    /// Seeking is a stop + restart with a new From bound.
    /// </summary>
    public void Start(ReplayRequest request)
    {
        lock (_gate)
        {
            StopInternal();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _runTask = Task.Run(() => RunFileAsync(request, token), token);
        }
    }

    /// <summary>Stops the current replay, if any.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopInternal();
        }
        Report(Progress with { State = ReplayState.Stopped });
    }

    private void StopInternal()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _runTask = null;
    }

    private async Task RunFileAsync(ReplayRequest request, CancellationToken token)
    {
        try
        {
            var records = AuditLogReader.FilterWindow(
                AuditLogReader.ReadRecords(request.FilePath), request.From, request.To);

            await RunAsync(records, request.Speed, token);
        }
        catch (OperationCanceledException)
        {
            // Stopped by user — Stop() reports the state.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Replay of {File} failed", request.FilePath);
            Report(Progress with { State = ReplayState.Faulted, Error = ex.Message });
        }
    }

    /// <summary>
    /// Core replay loop, exposed for testing. Re-publishes each record on the REPLAY-namespaced
    /// topic, pacing publications by the recorded inter-message gaps divided by <paramref name="speed"/>
    /// (speed &lt;= 0 means max speed: no delay).
    /// </summary>
    public async Task RunAsync(IEnumerable<AuditRecord> records, double speed, CancellationToken token = default)
    {
        int published = 0;
        DateTimeOffset? previous = null;

        Report(new ReplayProgress(ReplayState.Playing, null, 0));

        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();

            // Never replay a replay: if the record was somehow captured with the REPLAY
            // prefix, skip it rather than stacking prefixes or replaying onto live topics.
            if (record.Topic.StartsWith(TopicPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (previous.HasValue && speed > 0)
            {
                var gap = record.Timestamp - previous.Value;
                if (gap > TimeSpan.Zero)
                {
                    await _delay(TimeSpan.FromTicks((long)(gap.Ticks / speed)), token);
                }
            }

            previous = record.Timestamp;

            // GUARD: replay is ALWAYS published on the REPLAY.* namespace, never the
            // original topic — replaying onto live topics would re-trigger reactions
            // and real hardware.
            string replayTopic = TopicPrefix + record.Topic;
            var envelope = BuildEnvelope(replayTopic, record);
            try
            {
                await _bus.PublishAsync(replayTopic, envelope, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // MessageBus adds OnlyOnFaulted continuations to the task list it awaits;
                // when a handler succeeds those continuations transition to Canceled and the
                // await surfaces a spurious TaskCanceledException. It is not a replay
                // cancellation — the message was dispatched — so keep replaying.
            }

            published++;
            Report(new ReplayProgress(ReplayState.Playing, record.Timestamp, published));
        }

        Report(new ReplayProgress(ReplayState.Completed, previous, published));
    }

    private static MessageEnvelope BuildEnvelope(string replayTopic, AuditRecord record)
    {
        // Re-hydrate typed payloads where possible so replay consumers can handle them
        // like live traffic; otherwise carry the recorded text rendering.
        object payload;
        if (AuditLogReader.TryParseFlowEvent(record, out var flowEvent))
        {
            payload = flowEvent;
        }
        else if (AuditLogReader.TryParseElementStatus(record, out var status))
        {
            payload = status;
        }
        else
        {
            payload = record.PayloadText;
        }

        var header = new MessageHeader
        {
            Source = SourceName,
            Metadata =
            {
                ["ReplayOriginalTopic"] = record.Topic,
                ["ReplayOriginalTimestamp"] = record.Timestamp.ToString("O"),
                ["ReplayOriginalCorrelationId"] = record.CorrelationId ?? string.Empty
            }
        };

        return new MessageEnvelope(new MessageBusTopic(replayTopic), payload, client: SourceName, header: header);
    }

    private void Report(ReplayProgress progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke(progress);
    }
}
