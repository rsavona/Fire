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

    private PlcMessageParser _parser = new();

    private readonly ConcurrentDictionary<int, List<string>?> _ginRouting;
    private readonly ConcurrentDictionary<int, string?> _ginBarcode;
    public event Func<object, object, Task>? MessageReceived;

    public VirtualPlcDevice(
        IDeviceConfig config,
        IFireLogger logger,
        LoggingLevelSwitch levelSwitch)
        : base(config, logger, levelSwitch, true)
    {
        string? rawChainString = ConfigurationLoader.GetRequiredConfig<string>(Config.Properties, "DecisionPoints");

        if (rawChainString != null) _myChain = ParseChainFromString(rawChainString);

        _inductionFeq = ConfigurationLoader.GetRequiredConfig<int>(Config.Properties, "InductionFreq");
        _totalTotes = ConfigurationLoader.GetRequiredConfig<int>(Config.Properties, "TotalTotes");

        // Load optional barcode list
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
        }

        _ginBarcode = new ConcurrentDictionary<int, string?>();
        _ginRouting = new ConcurrentDictionary<int, List<string>?>();
    }


    /// <summary>
    /// Starts the device.
    /// </summary>
    /// <param name="ct"></param>
    protected override async Task OnStartAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);

        if (!ct.IsCancellationRequested)
        {
            while (Machine.State != State.Connected)
            {
                if (ct.IsCancellationRequested) return;

                await Task.Delay(1000, ct);

                Logger.Debug("[{Dev}] Still waiting for connection... Current State: {State}",
                    Config.Name, Machine.State);
            }

            Logger.Information("[{Dev}] Connection established. Launching simulation chain.", Config.Name);

            await RunChainSimulationAsync(_totalTotes, _inductionFeq, _myChain, ct);

            await Task.Delay(10000, ct);
        }
    }

    protected override Task HandleReceivedDataAsync(string incomingData)
    {
        Logger.Information("[{Dev}] Received : {Data}", Config.Name, incomingData);
        var msg = _parser.Parse(incomingData);
        if (msg is PlcMessage plcmsg && plcmsg.Payload is DecisionResponsePayload resp)
        {
            _ginRouting[resp.Gin] = resp.Actions;
            Logger.Information("[{Gin}] stored : {Action}", resp.Gin, resp.Actions);

            // Send DecisionUpdateMessage between 2 and 10 seconds randomly
            _ = Task.Run(async () =>
            {
                int delayMs = Random.Shared.Next(2000, 10001);
                await Task.Delay(delayMs);

                string actionTaken = resp.Actions.FirstOrDefault() ?? "UNKNOWN";
                var updateMsg = PlcMessageParser.CreateDecisionUpdate(Key.DeviceName, resp.DecisionPoint, resp.Gin, actionTaken, _ginBarcode.TryGetValue(resp.Gin, out var bc) ? new List<string> { bc } : null);

                await SendAsync(updateMsg.ToString(), CancellationToken.None);
                Logger.Information("[{Dev}] Sent DecisionUpdate for Gin: {Gin} Action: {Action} after {Delay}ms", Config.Name, resp.Gin, actionTaken, delayMs);
            });
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

                if (i == 0)
                {
                    // --- PHASE 0: INDUCT ---
                    targetStep = currentPhaseOptions.First();
                    await Task.Delay(targetStep.DistanceMs, token);
                  
                    conveyorClock.Start(); // Start clock the moment it passes induct
                    
                    string? barcode = null;
                    if (_barcodes != null && _barcodes.Count > 0)
                    {
                        int bcIndex = Interlocked.Increment(ref _barcodeIndex) % _barcodes.Count;
                        barcode = _barcodes[Math.Abs(bcIndex)];
                    }

                    var msg = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint, gin, barcode);
                    _ginBarcode[gin] = msg.GetBarcode();
                    var str = msg.ToString();
                    Logger.Information(" Gin: {gin} barcode: {barcode}", gin, _ginBarcode[gin]);
                    await SendAsync(str, token);

                    continue; // Move to the next phase in the chain
                }

                if (i == 1)
                {
                    if (currentPhaseOptions.Count == 1)
                        Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Message: {Msg}", Config.Name, gin,
                            currentPhaseOptions[0].DecisionPoint);
                    int divertDistanceMs = currentPhaseOptions.Min(p => p.DistanceMs);
                    long elapsedTravelMs = conveyorClock.ElapsedMilliseconds;
                    int remainingTravelMs = divertDistanceMs - (int)elapsedTravelMs;

                    if (remainingTravelMs > 0)
                    {
                        Logger.Debug("[PHASE 2 : {i}] Gin: {gin} Waiting for {ms}ms",i, gin,
                            remainingTravelMs);
                        // Tote is traveling. This gives the WCS time to populate the dictionary asynchronously.
                        await Task.Delay(remainingTravelMs, token);
                    }

                    if (!_ginRouting.TryGetValue(gin, out var routeList))
                    {
                        Logger.Warning("[PHASE 2 :gin was not in _ginRouting  {gin} count in list {count}",  gin, _ginRouting.Count());
                        continue;
                    }
                    Logger.Debug("[PHASE 2 : _ginRouting {ele} {gin} count in list {count}", routeList.FirstOrDefault() ,gin, _ginRouting.Count());

                    // 2. The tote has reached the physical divert. Determine the target.
                    if (currentPhaseOptions.Count == 0 || routeList == null)
                    {
                        continue;
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
                    continue;
                }

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