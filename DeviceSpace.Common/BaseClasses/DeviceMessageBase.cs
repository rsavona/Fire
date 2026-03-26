using System.Text.Json;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace DeviceSpace.Common.BaseClasses;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog; // Assuming Serilog based on your Log.Error syntax

public abstract record DeviceMessageBase : IDeviceMessage
{
   

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Instance method: Serializes the current message object.
    /// Uses GetType() to ensure derived properties are included.
    /// </summary>
    public string ToJson()
    {
        try
        {
            return JsonSerializer.Serialize(this, this.GetType(), _jsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Failed to serialize instance of {Type} to JSON", this.GetType().Name);
            return "{}";
        }
    }

    /// <summary>
    /// Static utility: Serializes any object to JSON.
    /// </summary>
    public static string ToJson<T>(T value) where T : class
    {
        if (value == null) return string.Empty;

        try
        {
            return JsonSerializer.Serialize(value, _jsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Failed to serialize {Type} to JSON", typeof(T).Name);
            return string.Empty;
        }
    }

    /// <summary>
    /// Static utility: Deserializes JSON string into a specific type.
    /// </summary>
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

    /// <summary>
    /// Wraps the message in a MessageEnvelope for the Message Bus.
    /// </summary>
    public virtual MessageEnvelope WrapMessage(MessageBusTopic t, int gin = 0, string client = "")
    {
        // 'this' passes the full derived record to the envelope
        return new MessageEnvelope(t, this, gin, client);
    }

    /// <summary>
    /// Outputs the message content to the logging system as JSON.
    /// </summary>
    public virtual void LogMessage()
    {
        Log.Information("[{MessageType}] Data: {Data}", this.GetType().Name, ToJson());
    }
    
    // Optional: Overriding ToString makes debugging in the IDE much easier
    public override string ToString() => ToJson();
}
