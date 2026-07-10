using Fusion.Common;
using Fusion.Element.Sparkplug.Protocol;

namespace Fusion.Element.Sparkplug;

/// <summary>
/// Maps Fusion element status snapshots onto Sparkplug device metrics.
/// Metric names use the Sparkplug folder convention ('/' separators) so they
/// group naturally in the Ignition tag browser.
/// </summary>
public static class SparkplugMetricMapper
{
    public static List<SparkplugMetric> FromStatus(ElementStatusMessage status)
    {
        ulong ts = status.Timestamp == default
            ? SparkplugMetric.NowMs()
            : (ulong)new DateTimeOffset(DateTime.SpecifyKind(status.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var metrics = new List<SparkplugMetric>
        {
            SparkplugMetric.Create("State", status.State, ts),
            SparkplugMetric.Create("Health", status.Health.ToString(), ts),
            SparkplugMetric.Create("Comment", status.Comment ?? string.Empty, ts),
            SparkplugMetric.Create("Counts/Inbound", (long)status.CountInbound, ts),
            SparkplugMetric.Create("Counts/Outbound", (long)status.CountOutbound, ts),
            SparkplugMetric.Create("Counts/Connections", (long)status.CountConnections, ts),
            SparkplugMetric.Create("Counts/Disconnects", (long)status.CountDisconnects, ts),
            SparkplugMetric.Create("Counts/Errors", (long)status.CountError, ts),
            SparkplugMetric.Create("Rates/InboundPerSec", status.InboundRate, ts),
            SparkplugMetric.Create("Rates/OutboundPerSec", status.OutboundRate, ts),
            SparkplugMetric.Create("Rates/AvgProcessTimeMs", status.AvgProcessTime, ts),
            SparkplugMetric.Create("Resources/Tasks", (long)status.ResourceTasks, ts),
            SparkplugMetric.Create("Resources/Containers", (long)status.ResourceContainers, ts),
            SparkplugMetric.Create("Resources/DeepCount", (long)status.ResourceDeepCount, ts)
        };

        foreach (var (name, value) in status.Metrics)
            metrics.Add(SparkplugMetric.Create($"Metrics/{name}", value, ts));

        return metrics;
    }
}

/// <summary>
/// Bus payload published on {EdgeNodeId}.SparkplugCmd.{MetricName} when Ignition
/// (or any Sparkplug host) writes an NCMD/DCMD metric to this edge node.
/// </summary>
public sealed record SparkplugCommandMessage
{
    /// <summary>Target device (Fusion element name) for DCMD; null for node-level NCMD.</summary>
    public string? DeviceId { get; init; }
    public string MetricName { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public string? Value { get; init; }
}
