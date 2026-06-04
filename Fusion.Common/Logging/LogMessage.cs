using System;
using System.Collections.Generic;
using Serilog.Events;

namespace Fusion.Common.Logging;

/// <summary>
/// Represents a single structured log entry in the Fusion ecosystem.
/// </summary>
public record LogMessage
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogEventLevel Level { get; init; }
    public string Context { get; init; } = string.Empty; // e.g., ElementName
    public string MessageTemplate { get; init; } = string.Empty;
    public object[] Args { get; init; } = [];
    public Exception? Exception { get; init; }
    public string FormattedMessage { get; init; } = string.Empty;

    /// <summary>
    /// Returns the topic identifier for this log message, used for bus routing.
    /// Format: LOG.{LEVEL}.{CONTEXT}
    /// </summary>
    public string GetTopic() => $"LOG.{Level.ToString().ToUpper()}.{Context.ToUpper()}";
}
