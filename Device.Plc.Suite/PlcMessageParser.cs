using System.Text.Json;
using System.Text.Json.Nodes;
using Device.Plc.Suite.Messages;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using Device.Plc.Suite.Messages;

namespace Device.Plc.Suite;


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
        int? specificGin = null, string? bc = null)
    {
        if (bc == null) bc = $"1D{Random.Shared.Next(10000, 99999)}";
        var bcList = new List<string> { bc };
        var payload = new DecisionRequestPayload(
            DecisionPoint: decisionPoint,
            Gin: specificGin ?? Random.Shared.Next(100000, 999999),
            Barcodes: bcList,
            Length: 10, Width: 10, Height: 10, Weight: 1.5,
            Metadata: new JsonObject { ["LanesActive"] = 4 },
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        )
        {
      
        };

        return new DecisionRequestMessage( device, GetNextSequenceNumber(), PlcMessageHeaders.DReqM, payload);
    }

    /// <summary>
    /// Generates a valid Decision Update for testing or simulation.
    /// </summary>
    public static DecisionUpdateMessage CreateDecisionUpdate(string device, string decisionPoint, int gin, string action, List<string>? barcodes = null)
    {
        var payload = new DecisionUpdatePayload(
            DecisionPoint: decisionPoint,
            Gin: gin,
            Barcodes: barcodes ?? new List<string> { "UNKNOWN" },
            ActionTaken: action,
            ReasonCode: 0,
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        );

        return new DecisionUpdateMessage(false, device, GetNextSequenceNumber(), PlcMessageHeaders.DUM, payload);
    }

    public static string CreateRawHeartbeat(string device)
    {
        var payload = new HeartbeatPayload();

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
        var json = JsonSerializer.Serialize(new HeartbeatPayload()
            { Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        return BuildFrame($"ACK{PlcControlChars.GS}{plcId}", GetNextSequenceNumber().ToString(),
            PlcMessageHeaders.HB.ToString(), json);
    }

    public static string FrameResponse(object payload, string deviceName)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return BuildFrame(deviceName, GetNextSequenceNumber().ToString(), PlcMessageHeaders.DRespM.ToString(), json);
    }

    public static string FrameUpdate(object payload, string deviceName)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return BuildFrame(deviceName, GetNextSequenceNumber().ToString(), PlcMessageHeaders.DUM.ToString(), json);
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