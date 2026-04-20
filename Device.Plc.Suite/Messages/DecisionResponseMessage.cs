using System.Text.Json.Serialization;

namespace Device.Plc.Suite.Messages;

public record DecisionResponseMessage : PlcMessage
{
     public DecisionResponseMessage( string client, int seq, PlcMessageHeaders hdr,
        DecisionResponsePayload payload)
        : base(false, client, seq, hdr.ToString(), payload)
    {
    }
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
