using System.Text.Json.Nodes;

namespace DeviceSpace.Common;

/// <summary>
/// Defines a parser capable of converting generic JbusMessage envelopes
/// into specific strongly-typed application messages.
/// </summary>
public interface IJBusMessageParser
{
    /// <summary>
    /// Determines if this parser can handle the given message type.
    /// </summary>
    bool CanParse(string messageType);

    /// <summary>
    /// Parses the JbusMessage envelope into a specific domain object.
    /// </summary>
    object? Parse(JBusMessage message);
}

/// <summary>
/// A specialized implementation of a parser for JBUS-based Device Managers.
/// </summary>
public class DefaultJbusMessageParser : IJBusMessageParser
{
    public bool CanParse(string messageType) => 
        messageType == "LabelRequest" || 
        messageType == "PlcCommand" || 
        messageType == "RegisterRead";

    public object? Parse(JBusMessage message)
    {
        try
        {
            if (!CanParse(message.PayloadType)) return null;

            var payload = message.Payload;
            
            switch (message.PayloadType)
            {
                case "LabelRequest":
                    return ExtractLabelRequest(payload, message.CustomStrId);
                
                case "RegisterRead":
                    // Logic to handle register data mapping
                    return payload.ToString(); 
                
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private object? ExtractLabelRequest(JsonObject payload, string deviceName)
    {
        var barcodes = new List<string>();
        if (payload["Barcodes"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node != null) barcodes.Add(node.ToString());
            }
        }

        // This effectively replaces the logic found in your previous PlcManager
        // while utilizing the new JbusMessage envelope structure.
        return new 
        {
            Device = deviceName,
            Barcodes = barcodes,
            Timestamp = DateTime.UtcNow,
            RawData = payload
        };
    }
}