using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Fusion.Common;
using Fusion.Common.Messaging;


namespace Fusion.Core;

public class BusAuditLogger : BackgroundService
{
    // We use the generic ILogger. Serilog intercepts this.
    private readonly ILogger<BusAuditLogger> _logger;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, every payload is captured as a fully structured object ({@Payload})
    /// in the audit CLEF stream, making it losslessly replayable by <see cref="Replay.ReplayService"/>.
    /// Default is false (payloads are captured as their ToString() rendering) because full
    /// destructuring can be expensive on disk for large payloads.
    /// Configure via "Fusion:AuditCapturePayloads" (or "AppSettings:Fusion:AuditCapturePayloads").
    /// Small replay-critical payloads (FlowEvent) are always captured structured regardless of this flag.
    /// </summary>
    public bool CapturePayloads { get; set; }

    // Configuration: Ignore these types to save disk space
    private readonly HashSet<string> _ignoredTypes = new()
    {
        "Heartbeat",
        "KeepAlive",
     };

    public BusAuditLogger(ILogger<BusAuditLogger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Call this to log a message from the bus.
    /// The 'Async' sink in Serilog ensures this does not block the calling thread.
    /// </summary>
    public void LogMessage(object? message, MessageHeader header, string topic = "Global")
    {
        if (message == null || !IsEnabled) return;

        // Recursion guard: curated log republishes (SYS.LOG.{Element}.{Level}) must never be
        // audited — auditing them would generate log events that get republished again,
        // creating a feedback loop between the logging pipeline and the audit log.
        if (topic.StartsWith("SYS.LOG", StringComparison.OrdinalIgnoreCase)) return;

        // Replay guard: messages re-published by the ReplayService on REPLAY.* topics must
        // never be audited. Auditing a replay would write the replayed traffic back into the
        // audit log, polluting the historical record and enabling replay-of-replay loops.
        if (topic.StartsWith("REPLAY.", StringComparison.OrdinalIgnoreCase)) return;

        string messageType = message.GetType().Name;

        // Filter Noise
        if (_ignoredTypes.Contains(messageType)) return;

        // Log using the "AuditLog" property.
        // The Serilog config in Program.cs looks for this property
        // to bond it to the specific audit file.
        var scopeProperties = new Dictionary<string, object>
        {
            { "AuditLog", true },
            { "CorrelationId", header.CorrelationId },
            { "MessageId", header.MessageId }
        };

        using (_logger.BeginScope(scopeProperties))
        {
            // Structured template: Topic and PayloadType land as first-class CLEF properties
            // so the audit log is machine-parseable (see Fusion.Core.Replay.AuditLogReader).
            if (CapturePayloads || message is FlowEvent)
            {
                // "{@Payload}" destructures the object into structured JSON — lossless replay.
                _logger.LogInformation("[{Topic,-40}] [{PayloadType,-15}]  {@Payload}", topic, messageType, message);
            }
            else
            {
                _logger.LogInformation("[{Topic,-40}] [{PayloadType,-15}]  {Payload}", topic, messageType, message.ToString());
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service is now purely reactive, but we keep it as a BackgroundService
        // in case we want to add periodic stats (e.g. "Logged 5000 messages this hour")
        _logger.LogInformation("Bus Audit Logger Service started.");
        return Task.CompletedTask;
    }
}
