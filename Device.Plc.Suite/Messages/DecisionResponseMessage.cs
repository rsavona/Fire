using System.Text.Json.Serialization;

namespace Device.Plc.Suite.Messages;

public class DecisionResponsePayload
{
    
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
