using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Fusion.Core;

/// <summary>
/// A high-performance structured logging bus using System.Threading.Channels.
/// Runs as a background service to dispatch logs asynchronously.
///
/// This is Tier 1 of the two-tier logging design: the raw log firehose. It is
/// deliberately NOT durable — the channel is in-memory, bounded, and drops the
/// oldest messages under back-pressure. Durability is a sink concern (the Logger
/// element / Serilog file sinks), not a bus concern.
/// </summary>
public class LoggingBus : BackgroundService, ILoggingBus
{
    /// <summary>Default channel capacity when none is configured ("Fusion:LoggingBusCapacity").</summary>
    public const int DefaultCapacity = 10_000;

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<LogMessage> _channel;
    private readonly ConcurrentDictionary<string, List<Func<LogMessage, CancellationToken, Task>>> _subscriptions;
    private readonly ConcurrentDictionary<string, List<Func<LogMessage, CancellationToken, Task>>> _matchCache;

    private long _droppedMessageCount;
    private long _lastDropWarningTicks;

    /// <summary>Configured channel capacity.</summary>
    public int Capacity { get; }

    /// <summary>Total number of log messages dropped because the channel was full.</summary>
    public long DroppedMessageCount => Interlocked.Read(ref _droppedMessageCount);

    public LoggingBus() : this(DefaultCapacity)
    {
    }

    public LoggingBus(int capacity)
    {
        Capacity = capacity > 0 ? capacity : DefaultCapacity;

        // Bounded, drop-oldest: logging must never block or balloon memory. Losing the
        // oldest verbose chatter under a burst is preferable to stalling element threads.
        _channel = Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true, // We use one background loop to dispatch
            SingleWriter = false, // Multiple loggers will write
            FullMode = BoundedChannelFullMode.DropOldest
        }, OnMessageDropped);

        _subscriptions = new ConcurrentDictionary<string, List<Func<LogMessage, CancellationToken, Task>>>(StringComparer.OrdinalIgnoreCase);
        _matchCache = new ConcurrentDictionary<string, List<Func<LogMessage, CancellationToken, Task>>>(StringComparer.OrdinalIgnoreCase);
    }

    private void OnMessageDropped(LogMessage dropped)
    {
        long count = Interlocked.Increment(ref _droppedMessageCount);

        // Periodic warning via the DIRECT Serilog logger — never via the bus itself,
        // which would only add pressure to the full channel (feedback loop).
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastDropWarningTicks);
        if (now - last >= DropWarningInterval.Ticks &&
            Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) == last)
        {
            Log.Warning(
                "[LoggingBus] Channel full (capacity {Capacity}); dropping oldest log messages. Total dropped so far: {DroppedCount}",
                Capacity, count);
        }
    }

    public async Task PublishAsync(LogMessage logMessage, CancellationToken ct = default)
    {
        // With FullMode.DropOldest this completes synchronously and never blocks the caller.
        await _channel.Writer.WriteAsync(logMessage, ct);
    }

    public Task SubscribeAsync(string topicPattern, Func<LogMessage, CancellationToken, Task> handler)
    {
        var handlers = _subscriptions.GetOrAdd(topicPattern, _ => []);
        lock (handlers)
        {
            handlers.Add(handler);
        }
        _matchCache.Clear(); // Invalidate cache on new subscription
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One reader loop for maximum speed
        await foreach (var msg in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var topic = msg.GetTopic();
                var handlers = GetHandlersForTopic(topic);

                if (handlers.Count > 0)
                {
                    // Fire and forget dispatch to not block the main reader channel
                    _ = Task.Run(async () =>
                    {
                        foreach (var handler in handlers)
                        {
                            try
                            {
                                await handler(msg, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                // Fallback to console/serilog if a log handler fails to avoid losing the log
                                Console.WriteLine($"[LoggingBus] Handler failed: {ex.Message}");
                            }
                        }
                    }, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingBus] Dispatch error: {ex.Message}");
            }
        }
    }

    private List<Func<LogMessage, CancellationToken, Task>> GetHandlersForTopic(string topic)
    {
        if (_matchCache.TryGetValue(topic, out var cached)) return cached;

        var result = new List<Func<LogMessage, CancellationToken, Task>>();

        foreach (var sub in _subscriptions)
        {
            if (IsTopicMatch(topic, sub.Key))
            {
                result.AddRange(sub.Value);
            }
        }

        _matchCache.TryAdd(topic, result);
        return result;
    }

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
}
