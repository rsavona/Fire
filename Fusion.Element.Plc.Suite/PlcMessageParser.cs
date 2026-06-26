using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Element.Plc.Suite.Messages;
using Fusion.Common;
using Fusion.Common.Contracts;


namespace Fusion.Element.Plc.Suite;


#region --- PLC Unified Parser & Factory ---

public class PlcMessageParser : IMessageParser
{
    private static int _sequenceNumber = 0;
    private static int _barcodeCounter = 0;
    private static JsonSerializerOptions _jsonOptions;
    private static readonly Random _rand = new();
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public PlcMessageParser()
    {
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    public bool CanHandle(SourceIdentifier source)
    {
        return source.SourcePath.StartsWith("PLC.", StringComparison.OrdinalIgnoreCase) ||
               source.SourcePath.EndsWith(".Request", StringComparison.OrdinalIgnoreCase);
    }

    public static DecisionRequestMessage CreateDecisionRequest(string element, string decisionPoint = "DP_SORTER_01",
        int? specificGin = null, string? bc = null, JsonObject? metadata = null,
        string printer1 = "PNA2_151", string printer2 = "PNA2_152")
    {
        int gin = specificGin ?? Random.Shared.Next(100000, 999999);
        
        var bcList = new List<string>();
        if (gin == 300)
        {
            // Special requested Error Scenario: Multi-barcode failure
            bcList.Add("????");  // No-read
            bcList.Add("*****"); // Side-by-side
        }
        else
        {
            if (bc == null)
            {
                int nextBc = Interlocked.Increment(ref _barcodeCounter);
                bc = $"LPN-{nextBc:D4}";
            }
            bcList.Add(bc);
        }

        if (metadata == null)
        {
            if (decisionPoint.Contains("PNA", StringComparison.OrdinalIgnoreCase))
            {
                // Metadata State Machine based on GIN
                string p1 = "1";
                string p2 = "1";

                // Global Override: GIN 360
                if (gin == 360)
                {
                    p1 = "1";
                    p2 = "1";
                }
                else if (gin >= 100 && gin < 325)
                {
                    // 60-GIN cycle (6 phases of 10 GINs each)
                    int offsetGin = gin - 100;
                    int cycleIndex = (offsetGin / 10) % 6;

                    switch (cycleIndex)
                    {
                        case 0: p1 = "0"; p2 = "1"; break; // Cycle 1: "0", "1"
                        case 1: p1 = "1"; p2 = "1"; break; // Cycle 2: "1", "1"
                        case 2: p1 = "1"; p2 = "0"; break; // Cycle 3: "1", "0"
                        case 3: p1 = "1"; p2 = "1"; break; // Cycle 4: "1", "1"
                        case 4: p1 = "0"; p2 = "0"; break; // Cycle 5: "0", "0"
                        case 5: p1 = "1"; p2 = "1"; break; // Cycle 6: "1", "1"
                    }
                }

                metadata = new JsonObject { [printer1] = p1, [printer2] = p2 };
            }
            else
            {
                metadata = new JsonObject { ["LanesActive"] = 4 };
            }
        }

        var payload = new DecisionRequestPayload(
            DecisionPoint: decisionPoint,
            Gin: gin,
            Barcodes: bcList,
            Length: 10, Width: 10, Height: 10, Weight: 1.5,
            Metadata: metadata,
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        )
        {

        };

        return new DecisionRequestMessage( element, GetNextSequenceNumber(), PlcMessageHeaders.DReqM, payload);
    }

    /// <summary>
    /// Generates a valid Decision Update for testing or simulation.
    /// </summary>
    public static DecisionUpdateMessage CreateDecisionUpdate(string element, string decisionPoint, int gin, string action, List<string>? barcodes = null, int reasonCode = 0)
    {
        var payload = new DecisionUpdatePayload(
            DecisionPoint: decisionPoint,
            Gin: gin,
            Barcodes: barcodes ?? new List<string> { "UNKNOWN" },
            ActionTaken: action,
            ReasonCode: reasonCode,
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        );

        return new DecisionUpdateMessage(false, element, GetNextSequenceNumber(), PlcMessageHeaders.DUM, payload);
    }

    public static string CreateRawHeartbeat(string element)
    {
        var payload = new HeartbeatPayload();

        // Uses the central BuildFrame to ensure consistency
        return BuildFrame(
            element,
            GetNextSequenceNumber().ToString("D8"),
            nameof(PlcMessageHeaders.HB),
            JsonSerializer.Serialize(payload, _jsonOptions)
        );
    }

    public static PlcMessage CreateTestMessage(string element, TestMessagePayload payload)
    {
        return new PlcMessage(false, element, GetNextSequenceNumber(), nameof(PlcMessageHeaders.TEST), payload);
    }

    public static PlcMessage CreateTestEndMessage(string element, TestMessagePayload payload)
    {
        return new PlcMessage(false, element, GetNextSequenceNumber(), nameof(PlcMessageHeaders.TESTEND), payload);
    }


    public object Parse(string rawPayload)
    {
        if (TryParseToPlcMessage(rawPayload, out var parsedMessage)) return parsedMessage!;
        throw new FormatException($"Raw payload '{rawPayload}' did not match expected PLC framing [STX...GS...ETX].");
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
                nameof(PlcMessageHeaders.TEST) => JsonSerializer.Deserialize<TestMessagePayload>(jsonPayload,
                    _jsonOptions),
                nameof(PlcMessageHeaders.TESTEND) => JsonSerializer.Deserialize<TestMessagePayload>(jsonPayload,
                    _jsonOptions),
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

    public static string FrameResponse(object payload, string elementName)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return BuildFrame(elementName, GetNextSequenceNumber().ToString(), PlcMessageHeaders.DRespM.ToString(), json);
    }

    public static string FrameUpdate(object payload, string elementName)
    {
        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return BuildFrame(elementName, GetNextSequenceNumber().ToString(), PlcMessageHeaders.DUM.ToString(), json);
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

    public static string BuildFrameFromMessage(string element, DecisionRequestMessage msg)
    {
        // It now leverages the centralized BuildFrame logic
        return BuildFrame(
            element,
            msg.SequenceNumber.ToString("D8"),
            msg.Header.ToString(),
            JsonSerializer.Serialize(msg.Payload, _jsonOptions)
        );
    }
}

#endregion
