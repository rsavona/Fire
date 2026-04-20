using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSpace.Common.BaseClasses;

namespace Device.Plc.Suite.Messages;


public enum PlcMessageHeaders
{
    DReqM, // Decision Request
    DUM, // Decision Update Message
    DRespM, // Decision Response Message
    HB // Heartbeat
}

public record PlcMessage : DeviceMessageBase
{
    [JsonIgnore] public string AckFlag { get; set; } = string.Empty;
    [JsonIgnore] public string ClientKey { get; set; } = string.Empty;
    [JsonIgnore] public int SequenceNumber { get; set; }
    [JsonIgnore] public PlcMessageHeaders Header { get; set; }
    public PlcPayloadBase Payload { get; init; }

    public PlcMessage(bool ack, string client, int seq, string header, PlcPayloadBase payload)
    {
        AckFlag = ack ? "ACK" : string.Empty;
        ClientKey = client;
        SequenceNumber = seq;
        Payload = payload;
        // Case-insensitive enum parsing
        Header = Enum.Parse<PlcMessageHeaders>(header, true);
    }
    public string GetBarcode()
    {
        if(Payload is DecisionRequestPayload pl)
            return pl.GetBarcode();
        return string.Empty;
    }
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"AckFlag = {AckFlag}, ");
        builder.Append($"ClientKey = {ClientKey}, ");
        builder.Append($"SequenceNumber = {SequenceNumber}, ");
        builder.Append($"Header = {Header}, ");

        // Serialize the payload to force the Barcode List (and any other derived properties) 
        // to render as a readable JSON string array like ["BC123", "BC456"]
        string payloadDisplay = JsonSerializer.Serialize(Payload);

        builder.Append($"Payload = {payloadDisplay}");

        return true;
    }

    public sealed override string ToString()
    { 
        string payloadJson = JsonSerializer.Serialize(Payload, Payload.GetType());

        return
            $"{PlcControlChars.STX}{ClientKey}{PlcControlChars.GS}{SequenceNumber:D8}{PlcControlChars.GS}{Header}{PlcControlChars.GS}{payloadJson}{PlcControlChars.ETX}";
    }
}

public abstract record PlcPayloadBase : DeviceMessageBase
{
    [JsonIgnore] public abstract PlcMessageHeaders Header { get; }
   
    // SEALED OVERRIDE: This forces ALL derived payloads (DecisionRequest, Heartbeat, etc.)
    // to stop using the ugly C# record format and print themselves as clean JSON instead.
    public sealed override string ToString()
    {
        // The (object) cast ensures the derived properties (Barcodes, GIN, etc.) are serialized
        return JsonSerializer.Serialize((object)this);
    }
    
}