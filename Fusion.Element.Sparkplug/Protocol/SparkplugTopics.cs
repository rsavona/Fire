namespace Fusion.Element.Sparkplug.Protocol;

/// <summary>
/// Builders and a parser for the Sparkplug B topic namespace:
/// spBv1.0/{group_id}/{message_type}/{edge_node_id}[/{device_id}]
/// </summary>
public static class SparkplugTopics
{
    public const string Namespace = "spBv1.0";

    public static string NodeBirth(string group, string node) => $"{Namespace}/{group}/NBIRTH/{node}";
    public static string NodeDeath(string group, string node) => $"{Namespace}/{group}/NDEATH/{node}";
    public static string NodeData(string group, string node) => $"{Namespace}/{group}/NDATA/{node}";
    public static string NodeCommand(string group, string node) => $"{Namespace}/{group}/NCMD/{node}";
    public static string DeviceBirth(string group, string node, string device) => $"{Namespace}/{group}/DBIRTH/{node}/{device}";
    public static string DeviceDeath(string group, string node, string device) => $"{Namespace}/{group}/DDEATH/{node}/{device}";
    public static string DeviceData(string group, string node, string device) => $"{Namespace}/{group}/DDATA/{node}/{device}";
    public static string DeviceCommandFilter(string group, string node) => $"{Namespace}/{group}/DCMD/{node}/+";

    public sealed record ParsedTopic(string Group, string MessageType, string EdgeNodeId, string? DeviceId);

    public static ParsedTopic? Parse(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 4 || parts[0] != Namespace) return null;
        return new ParsedTopic(parts[1], parts[2], parts[3], parts.Length > 4 ? string.Join('/', parts[4..]) : null);
    }
}
