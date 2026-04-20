using System.Text.Json.Serialization;

namespace Device.Plc.Suite.Messages;

public record DecisionUpdateMessage : PlcMessage
{
     public DecisionUpdateMessage(bool ack, string client, int seq, PlcMessageHeaders hdr,
        DecisionUpdatePayload payload)
        : base(ack, client, seq, hdr.ToString(), payload)
    {
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