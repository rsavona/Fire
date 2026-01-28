using System.Text.Json;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace DeviceSpace.Common.BaseClasses;

public abstract record DeviceMessageBase : IDeviceMessage
{
    public string MessageType { get; set; } = "Unknown";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Serializes the runtime type (correctly handles derived records)
    public virtual string ToJson() 
    {
        return JsonSerializer.Serialize(this, this.GetType(), _jsonOptions);
    }

   
    public virtual MessageEnvelope WrapMessage(MessageBusTopic t, int gin = 0, string client = "")
    {
        // By passing 'this', the envelope captures the specific derived type 
        // (like DecisionRequestMessage) and all its properties for the bus.
        return new MessageEnvelope(t, this, gin, client);
    }

    public virtual void LogMessage()
    {
        Log.Information("[{MessageType}] Data: {Data}", this.GetType().Name, ToJson());
    }

    // Static helper moves the generic to the method level
    public static T? FromJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Failed to deserialize {Type} from JSON", typeof(T).Name);
            return null;
        }
    }
}