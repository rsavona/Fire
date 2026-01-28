using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace Device.Plc.Suite;

#region --- PLC Message Definitions ---


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlcMessageHeaders
{
    DReqM, // Decision Request
    DUM, // Decision Update Message
    DRespM, // Decision Response Message
    HB // Heartbeat
}


public abstract record PlcPayloadBase
{
    [JsonIgnore] public abstract PlcMessageHeaders Header { get; }
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
        if (ack) AckFlag = "ACK";
        ClientKey = client;
        SequenceNumber = seq;
        Payload = payload;
        // Case-insensitive parsing to handle various PLC implementations
        Header = Enum.Parse<PlcMessageHeaders>(header, true);
        MessageType = Payload.Header.ToString();
    }
}

public record DecisionRequestMessage(bool ack, string client, int seq, PlcMessageHeaders hdr, PlcPayloadBase payload)
    : PlcMessage(ack, client, seq, hdr.ToString(), payload);

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

    private static DecisionResponsePayload CreateResponseFromRequest(DecisionRequestPayload req)
    {
        return new DecisionResponsePayload(req.DecisionPoint, req.Gin, new List<string>());
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
    [property: JsonPropertyName("GIN")] int Gin,
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

#region --- PLC Message Factory ---
public static class PlcMessageFactory
{
    private static readonly Random _randNumber = new();
    private static int _sequenceNumber = 0;
    private const int MaxSequence = 99999999;

    const char STX = '\x02'; 
    const char GS = '\x1D'; 
    const char ETX = '\x03'; 

        
    public static DecisionRequestMessage CreateDecisionRequest(string device, string decisionPoint = "DP_SORTER_01", int? specificGin = null)
    {
        var payload = new DecisionRequestPayload(
            DecisionPoint: decisionPoint,
            Gin: specificGin ?? _randNumber.Next(100000, 999999),
            Barcodes: new List<string> { $"1D{_randNumber.Next(10000, 99999)}" },
            Length: 10, Width: 10, Height: 10, Weight: 1.5,
            Metadata: new JsonObject { ["LanesActive"] = 4 },
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        );

        return new DecisionRequestMessage(true, device, GetNextSequenceNumber(), PlcMessageHeaders.DReqM, payload);
    }

    public static int GetNextSequenceNumber()
    {
        return Interlocked.Increment(ref _sequenceNumber);
    }

    public static string CreateRawHeartbeat(string device)
    {
        var payload = new HeartbeatPayload(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        return BuildFrame(device, GetNextSequenceNumber().ToString("D8"), PlcMessageHeaders.HB, payload);
    }

    public static string CreateDecisionUpdate(string device, string station, int gin, int seq)
    {
        var payload = new DecisionUpdatePayload(station, gin, new List<string>(), "COMPLETE", 0, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return BuildFrame(device, seq.ToString("D8"), PlcMessageHeaders.DUM, payload);
    }

    public static string BuildFrameFromMessage(string device, DecisionRequestMessage msg) 
        => BuildFrame(device, msg.SequenceNumber.ToString("D8"), msg.Header, msg.Payload);

    private static string BuildFrame(string device, string seq, PlcMessageHeaders header, PlcPayloadBase payload)
    {
       

        string json = JsonSerializer.Serialize(payload, payload.GetType());
        return $"{STX}{device}{GS}{seq}{GS}{header}{GS}{json}{ETX}";
    }
    
    /// <summary>
    /// Inspects an unframed message string to see if it is a Heartbeat ACK.
    /// </summary>
    public static bool IsHeartbeatAck(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        string cleanMessage = message.Trim(STX, ETX);
        var parts = cleanMessage.Split(GS, StringSplitOptions.RemoveEmptyEntries);
        var strHB = nameof(PlcMessageHeaders.HB);
        // Standard ACK Format: ACK [GS] PLCID [GS] Sequence [GS] HB [GS] JSON
        return parts.Length >= 4 && 
               parts[0] == "ACK" && 
               parts[3] == strHB;
    }
}


#endregion