using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace Fusion.Element.Logger;

/// <summary>
/// Tier-1 consumer of the two-tier logging design. Subscribes to the dedicated LoggingBus
/// (the raw log firehose) and writes every event to its own Serilog pipeline, preserving
/// structure (message template + named properties, not a flattened string).
///
/// It also performs the curated republish (Tier 2): events at or above
/// <c>RepublishMinimumLevel</c> (default Warning) whose origin topic matches
/// <c>RepublishTopicPattern</c> are published on the MAIN MessageBus as
/// <see cref="LogEventMessage"/> payloads on topics <c>SYS.LOG.{ElementName}.{Level}</c>.
/// The raw firehose itself is never routed through the MessageBus.
///
/// Recursion guards:
///  - All internal logging of this element's hot path goes to a direct Serilog logger,
///    never through the LoggingBus.
///  - Events originating from this element itself, or already carrying a SYS.LOG topic,
///    are never republished.
///  - Verbose bus dispatch tracing is safe: this element never republishes Verbose events
///    (default minimum is Warning; Verbose/Debug are rejected below the configured floor).
/// </summary>
public class SerilogSinkElement : ElementBase<SerilogSinkElement.State, SerilogSinkElement.Event, ElementMetric>
{
    public enum State { Offline, Listening, Faulted }
    public enum Event { Start, Stop, Error }

    private const int StatusPublishBatch = 250;
    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ILoggingBus _loggingBus;

    // Direct Serilog logger for the element's OWN diagnostics. Never IFireLogger here:
    // IFireLogger feeds the LoggingBus, and this element consumes the LoggingBus.
    private readonly Serilog.ILogger _internalLogger;

    private readonly string _topicPattern;
    private readonly LogEventLevel _republishMinimumLevel;
    private readonly string _republishTopicPattern;
    private readonly string _logFilePath;
    private readonly bool _writeToConsole;
    private readonly LogEventLevel _sinkMinimumLevel;

    private Serilog.Core.Logger? _sinkLogger;
    private bool _subscribed;
    private long _consumedSinceStatus;

    public long ConsumedCount { get; private set; }
    public long RepublishedCount { get; private set; }

    public SerilogSinkElement(IMessageBus bus, ILoggingBus loggingBus, IElementBlueprint config,
        IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, State.Offline, Event.Start)
    {
        _loggingBus = loggingBus;
        _internalLogger = Serilog.Log.Logger.ForContext("ElementName", config.Name);

        _topicPattern = GetProperty("TopicPattern", "#");
        _republishTopicPattern = GetProperty("RepublishTopicPattern", "#");
        _republishMinimumLevel = ParseLevel(GetProperty("RepublishMinimumLevel", "Warning"), LogEventLevel.Warning);
        _sinkMinimumLevel = ParseLevel(GetProperty("MinimumLevel", "Verbose"), LogEventLevel.Verbose);
        _writeToConsole = bool.TryParse(GetProperty("WriteToConsole", "false"), out var wc) && wc;
        _logFilePath = GetProperty("LogFilePath",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "logger-element", $"{config.Name}_.clef")));

        ConfigureStateMachine();
    }

    private string GetProperty(string key, string defaultValue)
    {
        return Config.Properties.TryGetValue(key, out var value)
            ? value?.ToString() ?? defaultValue
            : defaultValue;
    }

    private static LogEventLevel ParseLevel(string text, LogEventLevel fallback)
    {
        return Enum.TryParse<LogEventLevel>(text, true, out var level) ? level : fallback;
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .Permit(Event.Start, State.Listening);

        Machine.Configure(State.Listening)
            .Permit(Event.Stop, State.Offline)
            .Permit(Event.Error, State.Faulted);

        Machine.Configure(State.Faulted)
            .Permit(Event.Start, State.Listening)
            .Permit(Event.Stop, State.Offline);
    }

    public override async Task StartAsync(CancellationToken token)
    {
        try
        {
            BuildSinkLogger();

            if (!_subscribed)
            {
                // The LoggingBus has no unsubscribe; subscribe once per instance and gate
                // the handler on the state machine so a stopped element goes inert.
                await _loggingBus.SubscribeAsync(_topicPattern, HandleLogMessageAsync);
                _subscribed = true;
            }

            await Machine.FireAsync(Event.Start);
            _internalLogger.Information(
                "[{Dev}] Log sink online. Pattern={Pattern} Sink={SinkPath} RepublishMin={RepublishMin}",
                Config.Name, _topicPattern, _logFilePath, _republishMinimumLevel);
        }
        catch (Exception ex)
        {
            _internalLogger.Error(ex, "[{Dev}] Failed to start log sink element", Config.Name);
            Tracker.IncrementError(ex.Message);
            if (Machine.CanFire(Event.Error)) await Machine.FireAsync(Event.Error);
        }
    }

    public override async Task StopAsync(CancellationToken token)
    {
        if (Machine.CanFire(Event.Stop)) await Machine.FireAsync(Event.Stop);

        var sink = _sinkLogger;
        _sinkLogger = null;
        sink?.Dispose(); // flushes async file sinks

        _internalLogger.Information("[{Dev}] Log sink stopped. Consumed={Consumed} Republished={Republished}",
            Config.Name, ConsumedCount, RepublishedCount);
    }

    private void BuildSinkLogger()
    {
        _sinkLogger?.Dispose();

        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(_sinkMinimumLevel)
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                _logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14));

        if (_writeToConsole)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}][{Level:u3}][{ElementName}] {Message:lj}{NewLine}{Exception}");
        }

        _sinkLogger = configuration.CreateLogger();
    }

    /// <summary>
    /// Handles one raw log event from the LoggingBus firehose: writes it (structured) to
    /// the sink pipeline, then republishes it on the main MessageBus if it passes the
    /// curation filters. Public for testability.
    /// </summary>
    public async Task HandleLogMessageAsync(LogMessage message, CancellationToken ct)
    {
        if (Machine.State != State.Listening) return;

        try
        {
            var evt = message.Event ?? LogEventMessage.FromLogMessage(message);
            string originTopic = message.GetTopic();

            WriteStructured(evt);

            ConsumedCount++;
            Tracker.IncrementInbound();
            if (++_consumedSinceStatus >= StatusPublishBatch)
            {
                _consumedSinceStatus = 0;
                UpdateAndNotify();
            }

            if (ShouldRepublish(evt, originTopic))
            {
                RepublishedCount++;
                Tracker.IncrementOutbound();

                string topic = evt.GetRepublishTopic();
                await MessageBus.PublishAsync(topic, new MessageEnvelope(new MessageBusTopic(topic), evt), ct);
            }
        }
        catch (Exception ex)
        {
            // Direct Serilog only — never rethrow into the LoggingBus dispatch loop and
            // never log through IFireLogger from the sink path (recursion guard).
            Tracker.IncrementError(ex.Message);
            _internalLogger.Error(ex, "[{Dev}] Failed to process log event", Config.Name);
        }
    }

    /// <summary>
    /// Writes the event to the sink pipeline preserving structure: the original message
    /// template and named properties are reconstructed into a Serilog LogEvent.
    /// </summary>
    private void WriteStructured(LogEventMessage evt)
    {
        var sink = _sinkLogger;
        if (sink == null) return;

        try
        {
            string templateText = string.IsNullOrEmpty(evt.MessageTemplate)
                ? evt.RenderedMessage
                : evt.MessageTemplate;

            var template = TemplateParser.Parse(templateText ?? string.Empty);

            var properties = new List<LogEventProperty>
            {
                new("ElementName", new ScalarValue(evt.ElementName))
            };

            foreach (var kv in evt.Properties)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key == "ElementName") continue;
                properties.Add(new LogEventProperty(kv.Key, new ScalarValue(kv.Value)));
            }

            if (evt.Exception != null)
            {
                properties.Add(new LogEventProperty("ExceptionDetail", new ScalarValue(evt.Exception)));
            }

            var timestamp = evt.Timestamp.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(evt.Timestamp, TimeSpan.Zero)
                : new DateTimeOffset(evt.Timestamp);

            sink.Write(new LogEvent(timestamp, evt.Level, null, template, properties));
        }
        catch (Exception)
        {
            // Structural reconstruction failed — never lose the event, write it flat.
            sink.Write(evt.Level, "[{ElementName}] {RenderedMessage}", evt.ElementName, evt.RenderedMessage);
        }
    }

    /// <summary>
    /// Curation filter for the Tier-2 republish, including the recursion guards.
    /// </summary>
    public bool ShouldRepublish(LogEventMessage evt, string originTopic)
    {
        // Level floor (default Warning). This is also what keeps MessageBus verbose
        // dispatch tracing from feeding back: Verbose never crosses this floor by default.
        if (evt.Level < _republishMinimumLevel) return false;

        // Recursion guard: never republish our own internal events...
        if (string.Equals(evt.ElementName, Config.Name, StringComparison.OrdinalIgnoreCase)) return false;

        // ...or anything that already is (or derives from) a SYS.LOG republish.
        if (originTopic.Contains("SYS.LOG", StringComparison.OrdinalIgnoreCase)) return false;
        if (evt.ElementName.StartsWith("SYS.LOG", StringComparison.OrdinalIgnoreCase)) return false;

        return IsTopicMatch(originTopic, _republishTopicPattern);
    }

    /// <summary>
    /// Same wildcard semantics as the buses: '*' matches one segment, '#' the remainder.
    /// </summary>
    private static bool IsTopicMatch(string topic, string pattern)
    {
        if (pattern == "#" || pattern == "*") return true;

        var topicParts = topic.Split('.');
        var patternParts = pattern.Split('.');

        for (int i = 0; i < patternParts.Length; i++)
        {
            string p = patternParts[i];
            if (p == "#") return true;
            if (i >= topicParts.Length) return false;
            if (p == "*") continue;
            if (!string.Equals(p, topicParts[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return topicParts.Length == patternParts.Length;
    }

    protected override ElementHealth MapStateToHealth(State state) => state switch
    {
        State.Listening => ElementHealth.Normal,
        State.Faulted => ElementHealth.Critical,
        _ => ElementHealth.Warning
    };

    protected override void DisposeManagedResources()
    {
        _sinkLogger?.Dispose();
        _sinkLogger = null;
        base.DisposeManagedResources();
    }
}
