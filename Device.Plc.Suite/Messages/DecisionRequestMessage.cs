namespace Device.Plc.Suite.Messages;


public record DecisionRequestMessage : PlcMessage
{
    // Using a standard constructor prevents C# from auto-generating duplicate properties
    public DecisionRequestMessage(bool ack, string client, int seq, PlcMessageHeaders hdr,
        DecisionRequestPayload payload)
        : base(ack, client, seq, hdr.ToString(), payload)
    {
    }
}

public record DecisionRequestPayload(
    [property: JsonPropertyName("DecisionPoint")]
    string DecisionPoint,
    [property: JsonPropertyName("GIN")] int Gin,
    [property: JsonPropertyName("Barcodes")]
    List<string> Barcodes,
    [property: JsonPropertyName("Length")] int Length,
    [property: JsonPropertyName("Width")] int Width,
    [property: JsonPropertyName("Height")] int Height,
    [property: JsonPropertyName("Weight")] double Weight,
    [property: JsonPropertyName("Metadata")]
    JsonObject? Metadata,
    [property: JsonPropertyName("Timestamp")]
    string Timestamp
) : PlcPayloadBase
{
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.DReqM;
}








#endregion

#region --- PLC Message Models ---

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

    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"AckFlag = {AckFlag}, ");
        builder.Append($"ClientKey = {ClientKey}, ");
        builder.Append($"SequenceNumber = {SequenceNumber}, ");
        builder.Append($"Header = {Header}, ");

        // Serialize the payload to force the Barcode List (and any other derived properties) 
        // to render as a readable JSON string array like ["BC123", "BC456"]
        string payloadDisplay = Payload != null
            ? JsonSerializer.Serialize(Payload)
            : "null";

        builder.Append($"Payload = {payloadDisplay}");

        return true;
    }

    public sealed override string ToString()
    {
        string payloadJson = Payload != null
            ? JsonSerializer.Serialize(Payload, Payload.GetType())
            : "{}";

        // 2. Format the string exactly as requested
        return
            $"{PlcControlChars.STX}{ClientKey}{PlcControlChars.GS}{SequenceNumber:D8}{PlcControlChars.GS}{Header}{PlcControlChars.GS}{payloadJson}{PlcControlChars.ETX}";
    }
}



public record DecisionUpdatePayload(
    [property: JsonPropertyName("DecisionPoint")]
    string DecisionPoint,
    [property: JsonPropertyName("GIN")] int Gin,
    [property: JsonPropertyName("Barcodes")]
    List<string> Barcodes,
    [property: JsonPropertyName("ActionTaken")]
    string ActionTaken,
    [property: JsonPropertyName("ReasonCode")]
    int ReasonCode,
    [property: JsonPropertyName("Timestamp")]
    string Timestamp
) : PlcPayloadBase
{
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.DUM;
}

public record DecisionResponsePayload(
    [property: JsonPropertyName("DecisionPoint")]
    string DecisionPoint,
    [property: JsonPropertyName("GIN")] 
    int Gin,
    [property: JsonPropertyName("Actions")]
    List<string> Actions
) : PlcPayloadBase
{
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.DRespM;
}

public record HeartbeatPayload(
    [property: JsonPropertyName("Timestamp")]
    string Timestamp
) : PlcPayloadBase
{
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.HB;
}

#endregion