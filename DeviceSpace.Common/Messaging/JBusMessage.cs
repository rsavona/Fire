using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeviceSpace.Common;

/// <summary>
/// Represents a JBUS communication message, acting as the primary envelope 
/// for the internal message bus to interface with JBUS devices.
/// </summary>
public record JBusMessage
{
    public MessageBusTopic Destination { get; init; }
    public MessageBusTopic Source   { get; init;}
    public DateTime Created { get; init; } = DateTime.UtcNow;
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public string CustomStrId { get; init; }
    public int CustomIntId { get; init; }
    public bool IsHighPriority { get; init; }
    public string PayloadType { get; init; }
    public JsonObject Payload { get; init; }
 

    public JBusMessage(
        MessageBusTopic source,
        MessageBusTopic destination, 
        string strId,
        int intId,
        string messageType,
        JsonObject payload,
        bool highPriority = true)
    {
        Source = source;
        Destination = destination;
        CustomStrId = strId;
        CustomIntId = intId;
        PayloadType = messageType;
        Payload = payload;
        IsHighPriority = highPriority;
    }
public string Serialize()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>
    /// Deserializes a JSON string back into a JbusMessage instance.
    /// </summary>
    public static JBusMessage? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<JBusMessage>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }
}


