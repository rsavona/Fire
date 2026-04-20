using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSpace.Common.BaseClasses;

namespace Device.Plc.Suite.Messages;


public record DecisionRequestMessage : PlcMessage
{
    public DecisionRequestMessage(string client, int seq, PlcMessageHeaders hdr,
        DecisionRequestPayload payload)
        : base(false, client, seq, hdr.ToString(), payload)
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

    public string GetBarcode()
    {
        return Barcodes[0];
    }
}









