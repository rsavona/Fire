using System;
using System.Collections.Generic;

namespace Fusion.Common;

/// <summary>
/// Represents standard metadata headers for messages on the Fusion Message Bus.
/// </summary>
public record MessageHeader
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public string Source { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new();
}
