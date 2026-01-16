using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSpace.Common.BaseClasses;
// Assuming this exists in your project

namespace Device.Connector.Plc ;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlcMessageHeaders
{
    DReqM,   // Decision Request
    DUM,     // Decision Update Message
    DRespM,  // Decision Response Message
    HB,      // Heartbeat
    Heartbeat // Added alias in case the raw string comes in as "Heartbeat"
}

public abstract record PlcPayloadBase
{
    [JsonIgnore] 
    public abstract PlcMessageHeaders Header { get; }
}

public record PlcMessage : DeviceMessageBase
{
    [JsonIgnore] public string AckFlag { get; set; } = string.Empty;
    [JsonIgnore] public string ClientKey { get; set; } = string.Empty;
    [JsonIgnore] public int SequenceNumber { get; set; }
  
        
    [JsonIgnore] public PlcMessageHeaders Header { get; set; }

    // This holds the actual data (Heartbeat, Request, etc.)
    public PlcPayloadBase Payload { get; init; }

    // Constructor
    public PlcMessage(bool ack, string client, int seq, string  header, PlcPayloadBase payload) 
    {
        if (ack)
            AckFlag = "ACK";
            
        ClientKey = client;
        SequenceNumber = seq;
        Payload = payload;
        Header = Enum.Parse<PlcMessageHeaders>(header);
        MessageType = Payload.Header.ToString();
    }
}

public record DecisionRequestMessage : PlcMessage
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="request"></param>
    public DecisionRequestMessage(bool ack, string client, int seq, PlcMessageHeaders hdr,PlcPayloadBase payload) 
        : base(ack, client, seq, hdr.ToString(), payload)
    {
    }

}
      
public record DecisionResponseMessage : PlcMessage
{
    public DecisionResponseMessage(DecisionRequestMessage request)
        : base(
            ack: false,
            client: request.ClientKey,
            seq: request.SequenceNumber,
            header: PlcMessageHeaders.DRespM.ToString(),
            payload: CreateResponseFromRequest((DecisionRequestPayload)request.Payload)
        )
    {
    }

    private static DecisionResponsePayload CreateResponseFromRequest(PlcPayloadBase abstractPayload)
    {
        return abstractPayload is not DecisionRequestPayload req
            ? throw new ArgumentException("Payload must be of type DecisionRequestPayload")
            : new DecisionResponsePayload(req.DecisionPoint, req.Gin, new List<string>());
    }
}

public record DecisionRequestPayload(
    [property: JsonPropertyName("DecisionPoint")] string DecisionPoint,
    [property: JsonPropertyName("GIN")] int Gin,
    [property: JsonPropertyName("Barcodes")] List<string> Barcodes,
    [property: JsonPropertyName("Length")] int Length,
    [property: JsonPropertyName("Width")] int Width,
    [property: JsonPropertyName("Height")] int Height,
    [property: JsonPropertyName("Weight")] double Weight,
    [property: JsonPropertyName("Metadata")] JsonObject?  Metadata,
    [property: JsonPropertyName("Timestamp")] string Timestamp
) : PlcPayloadBase
{
    [JsonIgnore]
    public override PlcMessageHeaders Header => PlcMessageHeaders.DReqM;
}

// Decision Update (DUM)
public record DecisionUpdatePayload(
    [property: JsonPropertyName("DecisionPoint")] string DecisionPoint,
    [property: JsonPropertyName("GIN")] int Gin,
    [property: JsonPropertyName("Barcodes")] List<string> Barcodes,
    [property: JsonPropertyName("ActionTaken")] string ActionTaken,
    [property: JsonPropertyName("ReasonCode")] int ReasonCode,
    [property: JsonPropertyName("Timestamp")] string Timestamp
) : PlcPayloadBase
{
    [JsonIgnore]
    public override PlcMessageHeaders Header => PlcMessageHeaders.DUM;
}

public record DecisionResponsePayload : PlcPayloadBase
{
    [JsonPropertyName("DecisionPoint")]
    public string? DecisionPoint { get; init; }

    [JsonPropertyName("GIN")]
    public int Gin { get; init; }

    // Mutable Setter
    [JsonPropertyName("Actions")]
    public List<string> Actions { get; set; } = new();

    [JsonIgnore]
    public override PlcMessageHeaders Header => PlcMessageHeaders.DRespM;

    public DecisionResponsePayload(string decisionPoint, int gin, List<string> actions)
    {
        DecisionPoint = decisionPoint;
        Gin = gin;
        Actions = actions;
    }
    public DecisionResponsePayload() {}
}
public record HeartbeatPayload(
    [property: JsonPropertyName("Timestamp")] string Timestamp
) : PlcPayloadBase
{
    public override PlcMessageHeaders Header => PlcMessageHeaders.HB;
}