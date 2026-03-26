using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Plc.Suite;

#region --- PLC Protocol Constants & Enums ---

public enum PlcMessageHeaders
{
    DReqM, // Decision Request
    DUM, // Decision Update Message
    DRespM, // Decision Response Message
    HB // Heartbeat
}

public class AckPayload
{
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; } = string.Empty;
}

public record DecisionRequestMessage : PlcMessage
{
    // Using a standard constructor prevents C# from auto-generating duplicate properties
    public DecisionRequestMessage(bool ack, string client, int seq, PlcMessageHeaders hdr,
        DecisionRequestPayload payload)
        : base(ack, client, seq, hdr.ToString(), payload)
    {
    }
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

#region --- PLC Unified Parser & Factory ---

public class PlcMessageParser : IMessageParser
{
    private static int _sequenceNumber = 0;
    private static JsonSerializerOptions _jsonOptions;
    private static readonly Random _rand = new();

    public PlcMessageParser()
    {
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public bool CanHandle(SourceIdentifier source)
    {
        return source.SourcePath.StartsWith("PLC.", StringComparison.OrdinalIgnoreCase) ||
               source.SourcePath.EndsWith(".Request", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generates a valid Decision Request for testing or simulation.
    /// </summary>
    public static DecisionRequestMessage CreateDecisionRequest(string device, string decisionPoint = "DP_SORTER_01",
        int? specificGin = null)
    {
        var payload = new DecisionRequestPayload(
            DecisionPoint: decisionPoint,
            Gin: specificGin ?? Random.Shared.Next(100000, 999999),
            Barcodes: new List<string> { $"1D{Random.Shared.Next(10000, 99999)}" },
            Length: 10, Width: 10, Height: 10, Weight: 1.5,
            Metadata: new JsonObject { ["LanesActive"] = 4 },
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        )
        {
      
        };

        return new DecisionRequestMessage(true, device, GetNextSequenceNumber(), PlcMessageHeaders.DReqM, payload);
    }

    public static string CreateRawHeartbeat(string device)
    {
        var payload = new HeartbeatPayload(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

        // Uses the central BuildFrame to ensure consistency
        return BuildFrame(
            device,
            GetNextSequenceNumber().ToString("D8"),
            nameof(PlcMessageHeaders.HB),
            JsonSerializer.Serialize(payload, _jsonOptions)
        );
    }


    public object Parse(string rawPayload)
    {
        if (TryParseToPlcMessage(rawPayload, out var parsedMessage)) return parsedMessage!;
        throw new FormatException("Raw payload did not match expected PLC framing [STX...GS...ETX].");
    }

    public bool TryParseToPlcMessage(string rawMessage, out PlcMessage? parsedMessage)
    {
        parsedMessage = null;
        if (string.IsNullOrWhiteSpace(rawMessage)) return false;

        // Strip framing if present
        var clean = rawMessage.Trim(PlcControlChars.STX, PlcControlChars.ETX);
        var parts = clean.Split(PlcControlChars.GS, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 4) return false;

        string plcId = parts[0];
        if (!int.TryParse(parts[1], out int seq)) return false;
        string headerStr = parts[2];
        string jsonPayload = parts[3];

        try
        {
            PlcPayloadBase? payload = headerStr switch
            {
                nameof(PlcMessageHeaders.DReqM) => JsonSerializer.Deserialize<DecisionRequestPayload>(jsonPayload,
                    _jsonOptions),
                nameof(PlcMessageHeaders.DUM) => JsonSerializer.Deserialize<DecisionUpdatePayload>(jsonPayload,
                    _jsonOptions),
                nameof(PlcMessageHeaders.DRespM) => JsonSerializer.Deserialize<DecisionResponsePayload>(jsonPayload,
                    _jsonOptions),
                nameof(PlcMessageHeaders.HB) => JsonSerializer.Deserialize<HeartbeatPayload>(jsonPayload, _jsonOptions),
                _ => null
            };

            if (payload != null)
            {
                parsedMessage = new PlcMessage(plcId == "ACK", plcId, seq, headerStr, payload);
                return true;
            }
        }
        catch
        {
            /* Log failure if needed */
        }

        return false;
    }

    public string CreateHeartbeatAck(string plcId)
    {
        var json = JsonSerializer.Serialize(new AckPayload
            { Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        return BuildFrame($"ACK{PlcControlChars.GS}{plcId}", GetNextSequenceNumber().ToString(),
            PlcMessageHeaders.HB.ToString(), json);
    }

    public string FrameResponse(object payload, string deviceName)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return BuildFrame(deviceName, GetNextSequenceNumber().ToString(), PlcMessageHeaders.DRespM.ToString(), json);
    }

    public static int GetNextSequenceNumber() => Interlocked.Increment(ref _sequenceNumber);

    private static string BuildFrame(string prefix, string idOrSeq, string header, string json)
        =>
            $"{PlcControlChars.STX}{prefix}{PlcControlChars.GS}{idOrSeq}{PlcControlChars.GS}{header}{PlcControlChars.GS}{json}{PlcControlChars.ETX}";

    public static bool IsHeartbeatAck(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var parts = message.Trim(PlcControlChars.STX, PlcControlChars.ETX).Split(PlcControlChars.GS);
        return parts.Length >= 4 && parts[0] == "ACK" && parts[3] == nameof(PlcMessageHeaders.HB);
    }

    public static string BuildFrameFromMessage(string device, DecisionRequestMessage msg)
    {
        // It now leverages the centralized BuildFrame logic
        return BuildFrame(
            device,
            msg.SequenceNumber.ToString("D8"),
            msg.Header.ToString(),
            JsonSerializer.Serialize(msg.Payload, _jsonOptions)
        );
    }
}

#endregion