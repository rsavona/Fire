using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Serilog;
using Serilog.Core;
using System.Text.Json.Serialization;
using Device.Plc.Suite.Messages;

namespace Device.Plc.Suite.Virtual;

public class DecisionStep
{
    [JsonPropertyName("Name")] public string DecisionPoint { get; set; }

    public int DistanceMs { get; set; }
}

public class VirtualPlcDevice : TcpClientDeviceBase, IMessageProvider
{
    private CancellationTokenSource _simCts = new();
    private readonly int _inductionFeq;
    private readonly List<List<DecisionStep>> _myChain;
    private readonly int _totalTotes;
    private readonly int _heartbeatMs;
    private readonly List<string>? _barcodes;
    private int _barcodeIndex = -1;

    private int _manualGin = 9000;
    public void TriggerManualRelease()
    {
        int gin = Interlocked.Increment(ref _manualGin);
        Logger.Information("[{Dev}] MANUAL RELEASE triggered. GIN: {Gin}", Config.Name, gin);
        _ = Task.Run(() => ProcessToteLifecycleAsync(gin, _myChain, _simCts.Token));
    }

    private PlcMessageParser _parser = new();

    private readonly ConcurrentDictionary<int, List<string>?> _ginRouting;
    private readonly ConcurrentDictionary<int, string?> _ginBarcode;
    public event Func<object, object, Task>? MessageReceived;

    public VirtualPlcDevice(
        IMessageBus bus,
        IDeviceConfig config,
        IFireLogger logger,
        LoggingLevelSwitch levelSwitch)
        : base(bus, config, logger, levelSwitch, true)
    {
        // Robustly load DecisionPoints (support string, string list, or JsonElement array)
        string? rawChainString = null;
        if (Config.Properties.TryGetValue("DecisionPoints", out var dpObj))
        {
            if (dpObj is string dpStr)
            {
                rawChainString = dpStr;
            }
            else if (dpObj is IEnumerable<string> dpList)
            {
                rawChainString = string.Join(";", dpList);
            }
            else if (dpObj is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
                    rawChainString = string.Join(";", je.EnumerateArray().Select(e => e.GetString()));
                else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                    rawChainString = je.GetString();
            }
            else if (dpObj is object[] objArray)
            {
                rawChainString = string.Join(";", objArray.Select(o => o?.ToString() ?? ""));
            }
        }

        if (rawChainString != null)
        {
            Logger.Information("[{Dev}] DecisionPoints loaded: {Raw}", Config.Name, rawChainString);
            _myChain = ParseChainFromString(rawChainString);
            Logger.Information("[{Dev}] Parsed {Count} phases for simulation.", Config.Name, _myChain.Count);
        }
        else
        {
            Logger.Warning("[{Dev}] No DecisionPoints found in configuration.", Config.Name);
            _myChain = new List<List<DecisionStep>>();
        }

        _inductionFeq = ConfigurationLoader.GetOptionalConfig(Config.Properties, "InductionFreq", 1000);
        _totalTotes = ConfigurationLoader.GetOptionalConfig(Config.Properties, "TotalTotes", 0);

        // Load optional barcode list (support string, string list, or JsonElement array)
        if (Config.Properties.TryGetValue("BarcodeList", out var bcObj))
        {
            if (bcObj is string bcString)
            {
                _barcodes = bcString.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).ToList();
            }
            else if (bcObj is IEnumerable<string> bcList)
            {
                _barcodes = bcList.ToList();
            }
            else if (bcObj is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
                    _barcodes = je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                    _barcodes = je.GetString()?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim()).ToList();
            }
            else if (bcObj is object[] objArray)
            {
                _barcodes = objArray.Select(o => o?.ToString() ?? "").ToList();
            }
        }

        _ginBarcode = new ConcurrentDictionary<int, string?>();
        _ginRouting = new ConcurrentDictionary<int, List<string>?>();
    }


    /// <summary>
    /// Starts the device.
    /// </summary>
    /// <param name="ct"></param>
    protected override Task OnStartAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);
        return Task.CompletedTask;
    }

    protected override async Task DeviceConnectedAsync()
    {
        Logger.Information("[{Dev}] Connection established. Launching simulation chain.", Config.Name);
        
        // Cancel any previous simulation just in case
        await _simCts.CancelAsync();
        _simCts.Dispose();
        _simCts = new CancellationTokenSource();

        _ = Task.Run(() => RunChainSimulationAsync(_totalTotes, _inductionFeq, _myChain, _simCts.Token));
        
        await base.DeviceConnectedAsync();
    }

    protected override async Task DeviceDisconnectedAsync()
    {
        Logger.Warning("[{Dev}] Connection lost. Stopping simulation chain.", Config.Name);
        await _simCts.CancelAsync();
        await base.DeviceDisconnectedAsync();
    }

    protected override Task HandleReceivedDataAsync(string incomingData)
    {
        Logger.Information("[{Dev}] Received : {Data}", Config.Name, incomingData);
        var msg = _parser.Parse(incomingData);
        if (msg is PlcMessage plcmsg && plcmsg.Payload is DecisionResponsePayload resp)
        {
            _ginRouting[resp.Gin] = resp.DecisionPoints;
            Logger.Information("[{Gin}] stored : {Action}", resp.Gin, resp.DecisionPoints);

            // Execute DecisionUpdate functionality in parallel for each decision point in the list
            if (resp.DecisionPoints != null)
            {
                foreach (var dp in resp.DecisionPoints)
                {
                    _ = Task.Run(async () =>
                    {
                        // Generate a random delay between 2 and 10 seconds for each update
                        int delayMs = Random.Shared.Next(2000, 10001);
                        await Task.Delay(delayMs);

                        var updateMsg = PlcMessageParser.CreateDecisionUpdate(
                            Key.DeviceName,
                            resp.DecisionPoint,
                            resp.Gin,
                             dp ,
                            _ginBarcode.TryGetValue(resp.Gin, out var bc) ? new List<string> { bc } : null);

                        await SendAsync(updateMsg.ToString(), CancellationToken.None);
                        Logger.Information("[{Dev}] Sent DecisionUpdate for Gin: {Gin} Action: {Action} after {Delay}ms", 
                            Config.Name, resp.Gin, dp, delayMs);
                    });
                }
            }
        }
        else
        {
            Logger.Error("[{Dev}] Unknown message type: {type}", Config.Name, msg?.GetType().Name);
        }

        return Task.CompletedTask;
    }

    protected override bool IsHeartbeat(string incomingData)
    {
        return PlcMessageParser.IsHeartbeatAck(incomingData);
    }

    protected override string GetHeartbeatMessage()
    {
        return PlcMessageParser.CreateRawHeartbeat(Key.DeviceName);
    }

    public async Task RunChainSimulationAsync(
        int totalTotes,
        int inductIntervalMs,
        List<List<DecisionStep>> decisionPhases, // Updated to match your nested JSON structure
        CancellationToken token)
    {
        int currentGin = 0;

        try
        {
            while (!token.IsCancellationRequested && (totalTotes == 0 || currentGin < totalTotes))
            {
                currentGin++;

                // Fire and forget the lifecycle of THIS specific tote
                // This allows multiple totes to be "on the wire" at once
                _ = Task.Run(() => ProcessToteLifecycleAsync(currentGin, decisionPhases, token), token);

                // Wait for the next induction interval
                await Task.Delay(inductIntervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            /* Clean exit */
        }
    }

    // Define this at the class level for thread-safe round-robin routing
    private int _roundRobinIndex = 0;


    private async Task ProcessToteLifecycleAsync(int gin, List<List<DecisionStep>> phases, CancellationToken token)
    {
        if (phases == null || phases.Count == 0) return;

        var conveyorClock = new Stopwatch();
        string destination = null;
        
        try
        {
            for (int i = 0; i < phases.Count; i++)
            {
                Logger.Information("[Beginning of loop : phases {phases}] Gin: {gin} Message: {i}", phases.Count, gin, i);
                var currentPhaseOptions = phases[i];
                if (currentPhaseOptions.Count == 0) continue;

                DecisionStep? targetStep = null;

                switch (i)
                {
                    case 0:
                        // --- PHASE 0: INDUCT ---
                        targetStep = currentPhaseOptions.First();
                        await Task.Delay(targetStep.DistanceMs, token);

                        conveyorClock.Start(); // Start clock the moment it passes induct

                        // Make a barcode if we dont have one
                        string? barcode = null;
                        if (_barcodes != null && _barcodes.Count > 0)
                        {
                            int bcIndex = Interlocked.Increment(ref _barcodeIndex) % _barcodes.Count;
                            barcode = _barcodes[Math.Abs(bcIndex)];
                        }

                        // create the decision request message
                        var msg = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint, gin, barcode);
                        _ginBarcode[gin] = msg.GetBarcode();
                        var str = msg.ToString();
                        Logger.Information(" Gin: {gin} barcode: {barcode}", gin, _ginBarcode[gin]);
                        await SendAsync(str, token);
                        break;

                    case 1:
                        if (currentPhaseOptions.Count == 1)
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Message: {Msg}", Config.Name, gin,
                                currentPhaseOptions[0].DecisionPoint);
                        int divertDistanceMs = currentPhaseOptions.Min(p => p.DistanceMs);
                        long elapsedTravelMs = conveyorClock.ElapsedMilliseconds;
                        int remainingTravelMs = divertDistanceMs - (int)elapsedTravelMs;

                        if (remainingTravelMs > 0)
                        {
                            Logger.Debug("[PHASE 2 : {i}] Gin: {gin} Waiting for {ms}ms", i, gin,
                                remainingTravelMs);
                            // Tote is traveling. This gives the WCS time to populate the dictionary asynchronously.
                            await Task.Delay(remainingTravelMs, token);
                        }

                        if (!_ginRouting.TryGetValue(gin, out var routeList))
                        {
                            Logger.Warning("[PHASE 2 :gin was not in _ginRouting  {gin} count in list {count}", gin, _ginRouting.Count());
                            break;
                        }

                        Logger.Debug("[PHASE 2 : _ginRouting {ele} {gin} count in list {count}", routeList.FirstOrDefault(), gin, _ginRouting.Count());

                        // 2. The tote has reached the physical divert. Determine the target.
                        if (currentPhaseOptions.Count == 0 || routeList == null)
                        {
                            break;
                        }

                        var wantedStep = routeList.FirstOrDefault();
                        targetStep = currentPhaseOptions[0];
                        if (targetStep.DecisionPoint == wantedStep)
                        {
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                                targetStep.DecisionPoint);
                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint,
                                 gin, _ginBarcode[gin]);
                            // 4. Fire the PLC message for this specific step
                            await SendAsync(msgx.ToString(), token);
                            Logger.Debug("[PHASE 2 : sent: {msg}", msgx);
                        }

                        await Task.Delay(1000, token);
                        if (wantedStep == currentPhaseOptions[1].DecisionPoint)
                        {
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                               targetStep.DecisionPoint);
                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, wantedStep,
                                 gin, _ginBarcode[gin]);
                            // 4. Fire the PLC message for this specific step
                            await SendAsync(msgx.ToString(), token);

                        }
                        break;

                    default:
                        if (i == phases.Count - 1)
                        {
                            // 3. If the specific target is slightly further down the belt than the divert point, wait the delta.
                            int finalDeltaMs = 10000;
                            if (finalDeltaMs > 0)
                            {
                                await Task.Delay(finalDeltaMs, token);
                            }

                            targetStep = currentPhaseOptions.Last();
                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint,
                                gin, _ginBarcode[gin]);
                            Logger.Information("[PHASE 3 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                                   targetStep.DecisionPoint);
                            // 4. Fire the PLC message for this specific step
                            await SendAsync(msgx.ToString(), token);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected if the cancellation token is triggered during shutdown
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Lifecycle failed for GIN {Gin}  {ex}", Config.Name, gin, ex.Message);
        }
        finally
        {
            conveyorClock.Stop();
        }
    }

    // Helper method for round-robin selection
    private DecisionStep GetRoundRobinStep(List<DecisionStep> options)
    {
        // Safely increment and wrap the index for concurrent tote processing
        int index = Interlocked.Increment(ref _roundRobinIndex) % options.Count;
        return options[Math.Abs(index)];
    }

    /// <summary>
    /// Parses a string representation of the decision chain into a nested list structure.
    /// </summary>
    /// <param name="configString"></param>
    /// <returns></returns>
    public List<List<DecisionStep>> ParseChainFromString(string configString)
    {
        // chain string is in the format:   "DP_Induct:0; DP_PRINT1:1000 | DP_PRINT2:2000; DP_Verify:1000"
        var chain = new List<List<DecisionStep>>();


        if (string.IsNullOrWhiteSpace(configString)) return chain;

        // 1. Split the string into sequential physical phases
        string[] phases = configString.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string phaseStr in phases)
        {
            var currentPhaseOptions = new List<DecisionStep>();

            // 2. Split the phase into parallel "either/or" options
            string[] options = phaseStr.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string opt in options)
            {
                // 3. Split the Name from the Distance
                string[] parts = opt.Split(':');
                string name = parts[0].Trim();
                int distance = 0;

                // If they provided a distance, parse it. If not, it safely defaults to 0.
                if (parts.Length > 1)
                {
                    int.TryParse(parts[1].Trim(), out distance);
                }

                currentPhaseOptions.Add(new DecisionStep
                {
                    DecisionPoint = name,
                    DistanceMs = distance
                });
            }

            // Only add the phase to the chain if it actually contained valid options
            if (currentPhaseOptions.Count > 0)
            {
                chain.Add(currentPhaseOptions);
            }
        }

        return chain;
    }
}