using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Device.Plc.Suite;



 public class AckPayload
    {
        // The [JsonPropertyName] attribute ensures it looks exactly like your requirement
        [JsonPropertyName("Timestamp")] public string Timestamp { get; set; }
    }

public class PlcMessageParser
{
    // Control characters
    private const char STX = (char)0x02;
    private const char ETX = (char)0x03;
    private const char GS = (char)0x1D;


    // Message headers match the Enum strings or raw protocol strings
    private const string HEADER_DECISION_REQUEST = "DReqM";
    private const string HEADER_DECISION_RESPONSE = "DRespM";
    private const string HEADER_DECISION_UPDATE = "Dum";
    private const string HEADER_HEARTBEAT = "HB";

    private int _sequenceNumber = 0;
    private readonly JsonSerializerOptions _jsonOptions;


    public PlcMessageParser()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }


    /// <summary>
    /// Constructs a Heartbeat ACK message for the PLC.
    /// </summary>
    /// <param name="plcId">The ID of the PLC (e.g., PLCID001)</param>
    public string CreateHeartbeatAck(string plcId)
    {
        // Create the payload object
        var payload = new AckPayload
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        // Serialize the object to a JSON string
        string jsonPayload = JsonSerializer.Serialize(payload);

        // Build the full frame: [STX]ACK[GS]PLCID[GS]SEQ[GS]HB[GS]{"timestamp":"..."}[ETX]
        return $"{STX}ACK{GS}{plcId}{GS}{GetNextSequenceNumber()}{GS}HB{GS}{jsonPayload}{ETX}";
    }

   

    /// <summary>
    /// Frames the payload with the device name and sequence number.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="deviceName"></param>
    /// <returns></returns>
    public string FramePayload(string payload, string deviceName)
    {
        int seq = GetNextSequenceNumber();
        return $"{STX}{deviceName}{GS}{seq:D8}{GS}DRespM{GS}{payload}{ETX}";
    }


    /// <summary>
    /// Increments and returns the next sequence number in a thread-safe manner.
    /// </summary>
    public int GetNextSequenceNumber()
    {
        // Atomically increments the value and returns the new result
        return Interlocked.Increment(ref _sequenceNumber);
    }

    public bool TryParseToPlcMessage(string rawMessage, out PlcMessage? parsedMessage)
    {
        parsedMessage = null;

        if (string.IsNullOrWhiteSpace(rawMessage)) return false;

        string[] parts;
        try
        {
            parts = UnframeAndSplitByGs(rawMessage);
        }
        catch (FormatException)
        {
            return false;
        }

        // Expected format: PLCID [GS] Sequence [GS] Header [GS] JSON Payload
        if (parts.Length < 4) return false;

        string plcId = parts[0];
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seq)) return false;

        string headerStr = parts[2];
        string jsonPayload = parts[3];

        try
        {
            PlcPayloadBase? payload;

            // 1. Deserialize into the specific Payload Record
            switch (headerStr)
            {
                case HEADER_DECISION_REQUEST:
                    payload = JsonSerializer.Deserialize<DecisionRequestPayload>(jsonPayload, _jsonOptions);
                    break;

                case HEADER_DECISION_UPDATE:
                    payload = JsonSerializer.Deserialize<DecisionUpdatePayload>(jsonPayload, _jsonOptions);
                    break;

                case HEADER_DECISION_RESPONSE:
                    payload = JsonSerializer.Deserialize<DecisionResponsePayload>(jsonPayload, _jsonOptions);
                    break;

                case HEADER_HEARTBEAT:
                    payload = JsonSerializer.Deserialize<HeartbeatPayload>(jsonPayload, _jsonOptions);
                    break;

                default:
                    return false;
            }

            if (payload != null)
            {
                // Wrap in the new PlcMessageBase
                parsedMessage = new PlcMessage(
                    ack: false,
                    client: plcId,
                    seq: seq,
                    header: headerStr,
                    payload: payload
                );

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    private string[] UnframeAndSplitByGs(string unframedContent)
    {
        return unframedContent.Split(GS, StringSplitOptions.RemoveEmptyEntries);
    }
}