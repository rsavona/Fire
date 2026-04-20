using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceSpace.Common;

public readonly record struct SourceIdentifier(string DeviceKey, string SourcePath);

public record MessageEnvelope
{
    public MessageBusTopic Destination { get; init; }
    public int Gin { get; init; }
    public bool IsHighPriority { get; init; }
    public string Client { get; init; }
    public object Payload { get; init; }
    public DateTime Created { get; init; } = DateTime.UtcNow;

    // Use a single Primary Constructor or chain them
    public MessageEnvelope(MessageBusTopic dest, object payload, int gin = 0, string client = "", bool highPriority = true)
    {
        Destination = dest;
        Payload = payload;
        Gin = gin;
        Client = client;
        IsHighPriority = highPriority;
    }

    // Helper for when you only have a string topic
    public MessageEnvelope(string dest, object payload, int gin = 0, string client = "", bool highPriority = true)
        : this(new MessageBusTopic(dest), payload, gin, client, highPriority)
    {
    }
}


public static class MessageIdentifier
{
    /// <summary>
    /// Peeks into a payload to determine its "type" without full deserialization.
    /// </summary>
    public static string GetMessageType(object? payload)
    {
        if (payload == null) return "Null";

        // If it's already a string (JSON from ActiveMQ/PLC)
        if (payload is string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                // WCS standard: look for "type" or "MessageType"
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    return typeProp.GetString() ?? "Unknown";
                }
                if (doc.RootElement.TryGetProperty("MessageType", out var msgTypeProp))
                {
                    return msgTypeProp.GetString() ?? "Unknown";
                }
            }
            catch
            {
                return "InvalidJson";
            }
        }

        // Fallback: If it's a C# object (like a Record), return its class name
        return payload.GetType().Name;
    }
}
