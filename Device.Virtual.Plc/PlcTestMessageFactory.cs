using System.Text;
using System.Text.Json.Nodes;
using Device.Connector.Plc;
using Serilog;

namespace Device.Virtual.Plc;

public class PlcTestMessageFactory
{
    private static Random _rng = new Random();

    private readonly ILogger _logger; 
    private readonly string[] _stations = { "Induct", "BarcodePrinter", "InserterPrinter", "Verification" };
    private int _currentStationIndex = 0;
    private int _currentGin = 10001; 

    private string DeviceName;
    private int _sequenceNumber = 0;
    private const int MaxSequence = 99999999;

    // Events to notify the VirtualPlcDevice
    public event Action? HeartbeatReceived;
    public event Action<int, int, string>? DecisionResponseReceived; // Gin, Seq, Station

    public PlcTestMessageFactory(ILogger logger, string deviceName)
    {
        _logger = logger;
        DeviceName = deviceName;
    }

    /// <summary>
    /// Builds a standard Decision Request Message object.
    /// </summary>
    public DecisionRequestMessage CreateTestDecisionRequest(
        string decisionPoint = "DP_SORTER_01",
        int? specificGin = null)
    {
        var payload = new DecisionRequestPayload(
            DecisionPoint: decisionPoint,
            Gin: specificGin ?? _rng.Next(100000, 999999),
            Barcodes: new List<string> { $"1D{_rng.Next(10000, 99999)}", "AMZ-998234" },
            Length: _rng.Next(100, 500),
            Width: _rng.Next(100, 500),
            Height: _rng.Next(50, 200),
            Weight: Math.Round(_rng.NextDouble() * 10, 2),
            Metadata: new JsonObject { ["LanesActive"] = 4 },
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        );

        return new DecisionRequestMessage(
            ack: true,
            client: "PLC_TEST_CLIENT",
            seq: GetNextSequenceNumber(),
            hdr: PlcMessageHeaders.DReqM,
            payload: payload
        );
    }

    /// <summary>
    /// Generates a thread-safe incrementing sequence number.
    /// </summary>
    public int GetNextSequenceNumber()
    {
        int next;
        int current;
        do
        {
            current = _sequenceNumber;
            next = current >= MaxSequence ? 1 : current + 1;
        } while (Interlocked.CompareExchange(ref _sequenceNumber, next, current) != current);

        return next;
    }

    /// <summary>
    /// Creates the raw ASCII string for a Heartbeat.
    /// </summary>
    public string CreateRawHeartbeat(string deviceName)
    {
        const char STX = '\x02';
        const char GS = '\x1D';
        const char ETX = '\x03';
        string utcTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        return $"{STX}{deviceName}{GS}{GetNextSequenceNumber().ToString("D8")}{GS}HB{GS}{{ \"Timestamp\": \"{utcTime}\" }}{ETX}";
    }

    /// <summary>
    /// Creates a Decision Update message (DUM) to signal action completion.
    /// </summary>
    public string CreateDecisionUpdate(string station, int gin, int seq)
    {
        var payload = new DecisionUpdatePayload(
            DecisionPoint: station,
            Gin: gin,
            Barcodes: new List<string> { $"BC_{gin}_01" },
            ActionTaken: "COMPLETE", 
            ReasonCode: 0, 
            Timestamp: DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        );

        return BuildFrame(seq.ToString("D8"), PlcMessageHeaders.DUM, payload);
    }

    /// <summary>
    /// Helper to convert a message object into the wire-format ASCII frame.
    /// </summary>
    public string BuildFrameFromMessage(DecisionRequestMessage msg)
    {
        return BuildFrame(msg.SequenceNumber.ToString("D8"), msg.Header, msg.Payload);
    }

    private string BuildFrame(string seq, PlcMessageHeaders header, PlcPayloadBase payload)
    {
        const char STX = (char)0x02; 
        const char GS = (char)0x1D; 
        const char ETX = (char)0x03; 

        string json = System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType());

        StringBuilder sb = new StringBuilder();
        sb.Append(STX);
        sb.Append(DeviceName); 
        sb.Append(GS);
        sb.Append(seq); 
        sb.Append(GS);
        sb.Append(header.ToString());
        sb.Append(GS);
        sb.Append(json);
        sb.Append(ETX);

        return sb.ToString();
    }

    /// <summary>
    /// Parses incoming TCP data and triggers appropriate simulation logic.
    /// </summary>
    public async Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct)
    {
        string raw = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        
        // Log the raw incoming data for visibility
        _logger.Verbose("[Factory] Received: {Raw}", raw.Trim());

        string[] parts = raw.Split((char)0x1D); 
        if (parts.Length < 3) return;

        string headerStr = parts[3];
        
        // Use try-parse for safety with sequence numbers
        if (!int.TryParse(parts[2], out int incomingSeq)) incomingSeq = 0;

        // 1. Handle Heartbeat Acknowledgements
        if (headerStr == "ACK" || headerStr == "HB") 
        {
            _logger.Debug("[Factory] Heartbeat handshake validated.");
            HeartbeatReceived?.Invoke(); 
        }
        // 2. Handle Decision Responses from the WCS
        else if (headerStr == PlcMessageHeaders.DRespM.ToString())
        {
            _logger.Information("[Factory] Received Decision Response (Seq: {Seq})", incomingSeq);
            
            // In a real scenario, we would parse the JSON here to get the GIN
            // For the mimic, we notify the device to handle the next step
            DecisionResponseReceived?.Invoke(_currentGin, incomingSeq, "CurrentStation");
        }
    }
}