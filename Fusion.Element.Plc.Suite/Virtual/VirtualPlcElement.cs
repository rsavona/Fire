using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Serilog;
using Serilog.Core;
using System.Text.Json.Serialization;
using Fusion.Common;
using Fusion.Element.Plc.Suite.Messages;
using Fusion.Common.Configurations;
using Fusion.Common.TCP_Classes;

namespace Fusion.Element.Plc.Suite.Virtual;

public class DecisionStep
{
    [JsonPropertyName("CustomerName")] public string DecisionPoint { get; set; }

    public int DistanceMs { get; set; }
}

public class ScannerClientConfig
{
    public string DecisionPoint { get; set; } = string.Empty;
    public string IPAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public string TerminationChar { get; set; } = "\r";
}

public class VirtualPlcElement : TcpClientElementBase, IMessageProvider
{
    private CancellationTokenSource _simCts = new();
    private readonly int _inductionFeq;
    private readonly List<List<DecisionStep>> _myChain;
    private readonly int _totalTotes;
    private readonly List<string>? _barcodes;
    private int _barcodeIndex = -1;

    private readonly List<ScannerClientConfig> _scannerConfigs = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _scannerBuffers = new();
    private readonly List<Task> _scannerTasks = new();

    private int _manualGin = 9000;
    private int _currentGin = 0; // Elevated to class-level to survive disconnects

    public void TriggerManualRelease()
    {
        int gin = Interlocked.Increment(ref _manualGin);
        Logger.Information("[{Dev}] MANUAL RELEASE triggered. GIN: {Gin}", Config.Name, gin);
        _ = Task.Run(() => ProcessToteLifecycleAsync(gin, _myChain, _simCts.Token));
    }

    private PlcMessageParser _parser = new();

    private readonly ConcurrentDictionary<int, List<string>?> _ginRouting;
    private readonly ConcurrentDictionary<int, string?> _ginBarcode;
    private readonly ConcurrentDictionary<int, long> _decisionRequestTimestamps = new();
    private readonly string _printer1;
    private readonly string _printer2;
    public event Func<object, object, Task>? MessageReceived;

    public VirtualPlcElement(
        IMessageBus bus,
        IElementBlueprint config,
        IFireLogger logger,
        LoggingLevelSwitch levelSwitch)
        : base(bus, config, logger, levelSwitch, true)
    {
        _printer1 = ConfigurationLoader.GetOptionalConfig(Config.Properties, "Printer1", "PNA2_151");
        _printer2 = ConfigurationLoader.GetOptionalConfig(Config.Properties, "Printer2", "PNA2_152");

        // Load internal scanners
        if (Config.Properties.TryGetValue("Scanners", out var scannerObj))
        {
            try
            {
                var json = JsonSerializer.Serialize(scannerObj);
                var configs = JsonSerializer.Deserialize<List<ScannerClientConfig>>(json);
                if (configs != null) _scannerConfigs.AddRange(configs);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Failed to load scanner configurations.", Config.Name);
            }
        }

        foreach (var sc in _scannerConfigs)
        {
            _scannerBuffers.TryAdd(sc.DecisionPoint, new ConcurrentQueue<string>());
        }

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

        RegisterContainer(_scannerBuffers);
        RegisterContainer(_ginBarcode);
        RegisterContainer(_ginRouting);
    }


    /// <summary>
    /// Starts the element.
    /// </summary>
    /// <param name="ct"></param>
    protected override Task OnStartAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);
        
        foreach (var sc in _scannerConfigs)
        {
            _scannerTasks.Add(Task.Run(() => RunInternalScannerAsync(sc, _simCts.Token), _simCts.Token));
        }

        return Task.CompletedTask;
    }

    protected override async Task ElementConnectedAsync()
    {
        Logger.Information("[{Dev}] Connection established. Launching simulation chain.", Config.Name);
        
        // Cancel any previous simulation just in case
        await _simCts.CancelAsync();
        _simCts.Dispose();
        _simCts = new CancellationTokenSource();

        // Restart scanners with new token if they stopped
        if (_scannerTasks.Any(t => t.IsCompleted))
        {
            _scannerTasks.Clear();
            foreach (var sc in _scannerConfigs)
                _scannerTasks.Add(Task.Run(() => RunInternalScannerAsync(sc, _simCts.Token), _simCts.Token));
        }

        _ = Task.Run(() => RunChainSimulationAsync(_totalTotes, _inductionFeq, _myChain, _simCts.Token));
        
        await base.ElementConnectedAsync();
    }

    protected override async Task ElementDisconnectedAsync()
    {
        Logger.Warning("[{Dev}] Connection lost. Stopping simulation chain.", Config.Name);
        await _simCts.CancelAsync();
        await base.ElementDisconnectedAsync();
    }

    private async Task RunInternalScannerAsync(ScannerClientConfig config, CancellationToken ct)
    {
        Logger.Information("[{Dev}] Internal Scanner Client starting for {DP} -> {Host}:{Port}", 
            Config.Name, config.DecisionPoint, config.IPAddress, config.Port);

        byte[] delimiter = Encoding.ASCII.GetBytes(config.TerminationChar);
        var strategy = new DelimiterSetStrategy(delimiter);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(config.IPAddress, config.Port, ct);
                using var stream = client.GetStream();
                
                Logger.Information("[{Dev}] Internal Scanner connected for {DP}", Config.Name, config.DecisionPoint);

                var buffer = new byte[4096];
                var incoming = new List<byte>();

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) break;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = buffer[i];
                        incoming.Add(b);

                        var sequence = new ReadOnlySequence<byte>(incoming.ToArray());
                        var pos = strategy.FindTerminator(sequence);

                        if (pos != null)
                        {
                            string barcode = Encoding.ASCII.GetString(sequence.Slice(0, pos.Value).ToArray()).Trim();
                            if (_scannerBuffers.TryGetValue(config.DecisionPoint, out var queue))
                            {
                                queue.Enqueue(barcode);
                                Logger.Debug("[{Dev}] Internal Scanner {DP} buffered barcode: {BC}",
                                    Config.Name, config.DecisionPoint, barcode);
                            }
                            incoming.Clear();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warning("[{Dev}] Internal Scanner {DP} connection error: {Msg}. Retrying...", 
                    Config.Name, config.DecisionPoint, ex.Message);
                await Task.Delay(5000, ct);
            }
        }
    }

    private string? TryGetScannerBarcode(string dp)
    {
        if (_scannerBuffers.TryGetValue(dp, out var queue) && queue.TryDequeue(out var barcode))
        {
            return barcode;
        }
        return null;
    }

    protected override Task HandleReceivedDataAsync(string incomingData)
    {
        Logger.Information("[{Dev}] Received : {Data}", Config.Name, incomingData);
        var msg = _parser.Parse(incomingData);
        if (msg is PlcMessage plcmsg && plcmsg.Payload is DecisionResponsePayload resp)
        {
            _ginRouting[resp.Gin] = resp.DecisionPoints;
            Logger.Information("[{Gin}] stored : {Action}", resp.Gin, resp.DecisionPoints);

            // Calculate and log the round trip time
            if (_decisionRequestTimestamps.TryRemove(resp.Gin, out long startTimestamp))
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
                Tracker.AddProcessTime(elapsedMs);
                Logger.Information("[{Dev}] WCS Response Time for GIN {Gin}: {ElapsedMs:F2} ms", Config.Name, resp.Gin, elapsedMs);
            }

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

                        string? effectiveBarcode = GetEffectiveBarcode(resp.Gin, resp.DecisionPoint);
                        
                        string action = dp;
                        int reasonCode = 0;
                        
                        // Sorter Simulation Mode: Infer from name (not PNA)
                        if (!resp.DecisionPoint.Contains("PNA", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Random.Shared.Next(100) < 5) // 5% chance of mis-divert
                            {
                                action = "REJECT";
                                reasonCode = 99;
                            }
                        }

                        var updateMsg = PlcMessageParser.CreateDecisionUpdate(
                            Key.ElementName,
                            resp.DecisionPoint,
                            resp.Gin,
                            action,
                            effectiveBarcode != null ? new List<string> { effectiveBarcode } : null,
                            reasonCode);

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
        return PlcMessageParser.CreateRawHeartbeat(Key.ElementName);
    }

    public async Task RunChainSimulationAsync(
        int totalTotes,
        int inductIntervalMs,
        List<List<DecisionStep>> decisionPhases, // Updated to match your nested JSON structure
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && (totalTotes == 0 || _currentGin < totalTotes))
            {
                _currentGin++;

                SimulationCoordinator.UpdateGin(_currentGin);

                // Fire and forget the lifecycle of THIS specific tote
                // This allows multiple totes to be "on the wire" at once
                _ = Task.Run(() => ProcessToteLifecycleAsync(_currentGin, decisionPhases, token), token);

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

                        // Prioritize barcode from internal scanner
                        string? barcode = TryGetScannerBarcode(targetStep.DecisionPoint);
                        
                        if (barcode == null)
                        {
                            // Fallback to static list or generated SIM barcode
                            if (_barcodes != null && _barcodes.Count > 0)
                            {
                                int bcIndex = Interlocked.Increment(ref _barcodeIndex) % _barcodes.Count;
                                barcode = _barcodes[Math.Abs(bcIndex)];
                            }
                            else
                            {
                                barcode = $"SIM-{gin:D4}";
                            }
                        }

                        // create the decision request message
                        string? effectiveBarcode = GetEffectiveBarcode(gin, targetStep.DecisionPoint, barcode);
                        var msg = PlcMessageParser.CreateDecisionRequest(Key.ElementName, targetStep.DecisionPoint, gin, effectiveBarcode, null, _printer1, _printer2);
                        _ginBarcode[gin] = barcode;
                        var str = msg.ToString();
                        Logger.Information(" Gin: {gin} barcode: {barcode} (Source: {Src})", 
                            gin, _ginBarcode[gin], barcode.StartsWith("SIM-") ? "Generated" : "Scanner/List");
                        
                        _decisionRequestTimestamps[gin] = Stopwatch.GetTimestamp();
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

                        if (!_ginRouting.TryGetValue(gin, out var bondList))
                        {
                            Logger.Warning("[PHASE 2 :gin was not in _ginRouting  {gin} count in list {count}", gin, _ginRouting.Count());
                            break;
                        }

                        Logger.Debug("[PHASE 2 : _ginRouting {ele} {gin} count in list {count}", bondList.FirstOrDefault(), gin, _ginRouting.Count());

                        // 2. The tote has reached the physical divert. Determine the target.
                        if (currentPhaseOptions.Count == 0 || bondList == null)
                        {
                            break;
                        }

                        var wantedStep = bondList.FirstOrDefault();
                        targetStep = currentPhaseOptions[0];
                        if (targetStep.DecisionPoint == wantedStep)
                        {
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                                targetStep.DecisionPoint);
                            
                            // Check for downstream scanner update
                            string? scBarcode = TryGetScannerBarcode(targetStep.DecisionPoint);
                            if (scBarcode != null) _ginBarcode[gin] = scBarcode;

                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.ElementName, targetStep.DecisionPoint,
                                 gin, GetEffectiveBarcode(gin, targetStep.DecisionPoint), null, _printer1, _printer2);
                            // 4. Fire the PLC message for this specific step
                            _decisionRequestTimestamps[gin] = Stopwatch.GetTimestamp();
                            await SendAsync(msgx.ToString(), token);
                            Logger.Debug("[PHASE 2 : sent: {msg}", msgx);
                        }

                        await Task.Delay(1000, token);
                        if (currentPhaseOptions.Count > 1 && wantedStep == currentPhaseOptions[1].DecisionPoint)
                        {
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                               currentPhaseOptions[1].DecisionPoint);
                            
                            // Check for downstream scanner update
                            string? scBarcode = TryGetScannerBarcode(currentPhaseOptions[1].DecisionPoint);
                            if (scBarcode != null) _ginBarcode[gin] = scBarcode;

                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.ElementName, wantedStep,
                                 gin, GetEffectiveBarcode(gin, wantedStep), null, _printer1, _printer2);
                            // 4. Fire the PLC message for this specific step
                            _decisionRequestTimestamps[gin] = Stopwatch.GetTimestamp();
                            await SendAsync(msgx.ToString(), token);

                        }
                        break;

                    default:
                        if (i == phases.Count - 1)
                        {
                            targetStep = currentPhaseOptions.Last();
                            
                            // 3. Wait the specific travel time for this final physical step
                            if (targetStep.DistanceMs > 0)
                            {
                                await Task.Delay(targetStep.DistanceMs, token);
                            }

                            // Check for final scanner update
                            string? scBarcode = TryGetScannerBarcode(targetStep.DecisionPoint);
                            if (scBarcode != null) _ginBarcode[gin] = scBarcode;

                            var msgx = PlcMessageParser.CreateDecisionRequest(Key.ElementName, targetStep.DecisionPoint,
                                gin, GetEffectiveBarcode(gin, targetStep.DecisionPoint), null, _printer1, _printer2);
                            Logger.Information("[PHASE 3 : {Dev}] Gin: {gin} Target: {target}", Config.Name, gin,
                                   targetStep.DecisionPoint);
                            // 4. Fire the PLC message for this specific step
                            _decisionRequestTimestamps[gin] = Stopwatch.GetTimestamp();
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

    private string? GetEffectiveBarcode(int gin, string decisionPoint, string? barcodeOverride = null)
    {
        string? baseBarcode = barcodeOverride;
        if (baseBarcode == null && _ginBarcode.TryGetValue(gin, out var stored))
        {
            baseBarcode = stored;
        }

        if (baseBarcode != null && decisionPoint.Contains("Verify", StringComparison.OrdinalIgnoreCase))
        {
            return baseBarcode + "123";
        }
        return baseBarcode;
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
                // 3. Split the CustomerName from the Distance
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