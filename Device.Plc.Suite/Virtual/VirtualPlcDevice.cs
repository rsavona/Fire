using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Serilog;
using Serilog.Core;
using System.Text.Json.Serialization;

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

    private PlcMessageParser _parser = new();

    private readonly ConcurrentDictionary<int, List<string>?> _ginRouting;
    public event Func<object, object, Task>? MessageReceived;

    public VirtualPlcDevice(
        IDeviceConfig config,
        IFireLogger logger,
        LoggingLevelSwitch levelSwitch )
        : base(config, logger, levelSwitch, true)
    {
        
        string? rawChainString = ConfigurationLoader.GetRequiredConfig<string>(Config.Properties, "DecisionPoints");

        if (rawChainString != null) _myChain = ParseChainFromString(rawChainString);

        _inductionFeq = ConfigurationLoader.GetRequiredConfig<int>(Config.Properties, "InductionFreq");
        _totalTotes = ConfigurationLoader.GetRequiredConfig<int>(Config.Properties, "TotalTotes");

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
            while (Machine.State != State.Connected )
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
                var currentPhaseOptions = phases[i];
                if (currentPhaseOptions.Count == 0) continue;

                DecisionStep? targetStep = null;

                if (i == 0)
                {
                    // --- PHASE 0: INDUCT ---
                    targetStep = currentPhaseOptions.First();
                    await Task.Delay(targetStep.DistanceMs, token);

                    conveyorClock.Start(); // Start clock the moment it passes induct
                    var msg = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint, gin);
                    var str = msg.ToString();
                    Logger.Information("[{Dev}] Gin: {gin} Message: {Msg}", Config.Name, gin, str);
                    await SendAsync(str, token);

                    continue; // Move to the next phase in the chain
                }

                int divertDistanceMs = currentPhaseOptions.Min(p => p.DistanceMs);
                long elapsedTravelMs = conveyorClock.ElapsedMilliseconds;
                int remainingTravelMs = divertDistanceMs - (int)elapsedTravelMs;

                if (remainingTravelMs > 0)
                {
                    // Tote is traveling. This gives the WCS time to populate the dictionary asynchronously.
                    await Task.Delay(remainingTravelMs, token);
                }

                // 2. The tote has reached the physical divert. Determine the target.
                if (currentPhaseOptions.Count == 1)
                {
                    targetStep = currentPhaseOptions[0];
                }
                else
                {
                    // Check the dictionary for the GIN's route list
                    if (_ginRouting.TryGetValue(gin, out var routeList))
                    {
                        // We MUST lock the list because multiple async tasks or background threads
                        // cannot safely modify a standard List<T> at the exact same time.
                        lock (routeList)
                        {
                            if (routeList.Count > 0)
                            {
                                destination = routeList[0]; // Grab the first destination
                                routeList.RemoveAt(0); // Consume/remove it from the list

                                // If that was the last stop, clean up the dictionary to prevent memory leaks
                                if (routeList.Count == 0)
                                {
                                    _ginRouting.TryRemove(gin, out _);
                                }
                            }
                        }
                    }

                    // Attempt to match the string we just pulled from the list to an actual physical option
                    if (!string.IsNullOrEmpty(destination))
                    {
                        targetStep = currentPhaseOptions.FirstOrDefault(p => p.DecisionPoint == destination);
                    }

                    // Fallback: If the list was empty, missing, or gave us an invalid name
                    if (targetStep == null)
                    {
                        Logger.Warning(
                            "[{Dev}] GIN:{Gin} No route found in _ginRouting at divert point. Using round-robin.",
                            Config.Name, gin);
                        targetStep = GetRoundRobinStep(currentPhaseOptions);
                    }
                }

                // 3. If the specific target is slightly further down the belt than the divert point, wait the delta.
                elapsedTravelMs = conveyorClock.ElapsedMilliseconds;
                int finalDeltaMs = targetStep.DistanceMs - (int)elapsedTravelMs;

                if (finalDeltaMs > 0)
                {
                    await Task.Delay(finalDeltaMs, token);
                }

                var msgx = PlcMessageParser.CreateDecisionRequest(Key.DeviceName, targetStep.DecisionPoint,
                    gin);
                // 4. Fire the PLC message for this specific step
                await SendAsync(msgx.ToString(), token);
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


    public List<List<DecisionStep>> ParseChainFromString(string configString)
    {
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