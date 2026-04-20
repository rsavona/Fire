using System.Text.Json.Serialization;

namespace Device.Plc.Suite.Messages;


public record HeartbeatMessage : PlcMessage
{
    public HeartbeatMessage(bool ack, string client, int seq, PlcMessageHeaders hdr,
        HeartbeatPayload payload)
        : base(ack, client, seq, hdr.ToString(), payload)
    {
    }
}

public record AckHeartbeatMessage : HeartbeatMessage
{
    public AckHeartbeatMessage(string client, int seq, PlcMessageHeaders hdr, HeartbeatPayload payload)
        : base(true, client, seq, hdr, payload)
    {
    }
}


public record HeartbeatPayload : PlcPayloadBase
{

    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.HB;
     [property: JsonPropertyName("Timestamp")]
     
    public HeartbeatPayload()
    {
    }
}


