using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const int TestStartDelayMs = 5000;

    private static readonly ITerminationStrategy PlcFrameTerminationStrategy =
        new DelimiterSetStrategy((byte)PlcControlChars.ETX);

    private CancellationTokenSource _simCts = new();
    private readonly int _inductionFeq;
    private List<List<DecisionStep>> _myChain;
    private readonly int _totalTotes;
    private readonly int _decisionResponseTimeoutMs;
    private readonly List<string>? _barcodes;
    private int _barcodeIndex = -1;
    private readonly SemaphoreSlim _lifecycleOrderGate = new(1, 1);
    private readonly ConcurrentDictionary<(int Gin, string DecisionPoint), TaskCompletionSource<DecisionResponsePayload>>
        _pendingDecisionResponses = new();

    private readonly List<ScannerClientConfig> _scannerConfigs = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _scannerBuffers = new();
    private readonly List<Task> _scannerTasks = new();

    private int _manualGin = 9000;
    private int _currentGin = 0; // Elevated to class-level to survive disconnects

    protected override ITerminationStrategy? ReceiveTerminationStrategy => PlcFrameTerminationStrategy;

    public void TriggerManualRelease()
    {
        int gin = Interlocked.Increment(ref _manualGin);
        Logger.Information("[{Dev}] MANUAL RELEASE triggered. GIN: {Gin}", Config.Name, gin);
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessToteLifecycleInGlobalOrderAsync(gin, _myChain, _simCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal during shutdown or reconnect.
            }
        });
    }

    private PlcMessageParser _parser = new();

    private readonly ConcurrentDictionary<int, List<string>?> _ginRouting;
    private readonly ConcurrentDictionary<int, string?> _ginBarcode;
    private readonly ConcurrentDictionary<int, long> _decisionRequestTimestamps = new();
    private string _printer1;
    private string _printer2;
    private readonly ScriptedPlcScenario? _scriptedScenario;
    private readonly string? _scriptedScenarioPath;
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
        _decisionResponseTimeoutMs =
            ConfigurationLoader.GetOptionalConfig(Config.Properties, "DecisionResponseTimeoutMs", 15000);

        _scriptedScenarioPath = ConfigurationLoader.GetOptionalConfig<string?>(Config.Properties, "ScriptedScenarioFile", null);
        if (!string.IsNullOrWhiteSpace(_scriptedScenarioPath))
        {
            var resolvedScenarioPath = ResolveScenarioPath(_scriptedScenarioPath);
            if (resolvedScenarioPath == null)
            {
                Logger.Error("[{Dev}] Scripted scenario file was configured but not found: {Path}",
                    Config.Name, _scriptedScenarioPath);
            }
            else
            {
                _scriptedScenario = ScriptedPlcScenario.Load(resolvedScenarioPath);
                Logger.Information("[{Dev}] Loaded scripted PLC scenario {Scenario} from {Path}",
                    Config.Name, _scriptedScenario.Name, resolvedScenarioPath);

                var scenarioProperties = _scriptedScenario.VirtualPlcProperties;
                if (!string.IsNullOrWhiteSpace(scenarioProperties?.DecisionPoints))
                {
                    _myChain = ParseChainFromString(scenarioProperties.DecisionPoints);
                    Logger.Information("[{Dev}] Scripted scenario decision chain loaded: {Raw}",
                        Config.Name, scenarioProperties.DecisionPoints);
                }

                if (!string.IsNullOrWhiteSpace(scenarioProperties?.Printer1))
                {
                    _printer1 = scenarioProperties.Printer1;
                }

                if (!string.IsNullOrWhiteSpace(scenarioProperties?.Printer2))
                {
                    _printer2 = scenarioProperties.Printer2;
                }
            }
        }

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

        if (_scriptedScenario != null)
        {
            _ = Task.Run(() => RunScriptedScenarioAsync(_scriptedScenario, _myChain, _simCts.Token));
        }
        else
        {
            _ = Task.Run(() => RunChainSimulationAsync(_totalTotes, _inductionFeq, _myChain, _simCts.Token));
        }

        await base.ElementConnectedAsync();
    }

    protected override async Task ElementDisconnectedAsync()
    {
        Logger.Warning("[{Dev}] Connection lost. Stopping simulation chain.", Config.Name);
        await _simCts.CancelAsync();
        await base.ElementDisconnectedAsync();
    }

    private static string? ResolveScenarioPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        if (File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), configuredPath),
            Path.Combine(AppContext.BaseDirectory, configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), "Fusion.Reaction.Simulation", "TestCases", configuredPath),
            Path.Combine(AppContext.BaseDirectory, "TestCases", configuredPath)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var fileName = Path.GetFileName(configuredPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var root in EnumerateSearchRoots())
        {
            try
            {
                var match = Directory
                    .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(path =>
                        path.Contains($"{Path.DirectorySeparatorChar}TestCases{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    return Path.GetFullPath(match);
                }
            }
            catch
            {
                // Ignore roots we cannot enumerate.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current != null)
        {
            if (seen.Add(current.FullName))
            {
                yield return current.FullName;
            }

            current = current.Parent;
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (baseDirectory != null)
        {
            if (seen.Add(baseDirectory.FullName))
            {
                yield return baseDirectory.FullName;
            }

            baseDirectory = baseDirectory.Parent;
        }
    }

    private static JsonObject? CloneMetadata(JsonObject? source)
    {
        if (source == null)
        {
            return null;
        }

        return JsonNode.Parse(source.ToJsonString())?.AsObject();
    }

    private static string FormatSequenceAnomaly(string anomaly)
    {
        if (string.IsNullOrWhiteSpace(anomaly))
        {
            return anomaly;
        }

        var parts = anomaly.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && int.TryParse(parts[2], out _))
        {
            return $"{parts[0]} {parts[1].ToUpperInvariant()} {parts[2]}";
        }

        return anomaly.Replace('-', ' ');
    }

    private async Task RunInternalScannerAsync(ScannerClientConfig config, CancellationToken ct)
    {
        Logger.Information("[{Dev}] Internal Scanner Client starting for {DP} -> {Host}:{Port}",
            Config.Name, config.DecisionPoint, config.IPAddress, config.Port);

        byte[] delimiter = Encoding.ASCII.GetBytes(config.TerminationChar);
        var strategy = new SequenceTerminationStrategy(delimiter);

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
            catch (OperationCanceledException)
            {
                break;
            }
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

        if (!_parser.TryParseToPlcMessage(incomingData, out var msg) || msg == null)
        {
            Logger.Error("[{Dev}] Failed to parse PLC message: {Data}", Config.Name, incomingData);
            return Task.CompletedTask;
        }

        if (msg.Payload is not DecisionResponsePayload resp)
        {
            Logger.Error("[{Dev}] Unknown PLC payload type: {type}", Config.Name, msg.Payload.GetType().Name);
            return Task.CompletedTask;
        }

        _ginRouting[resp.Gin] = resp.DecisionPoints;
        Logger.Information("[{Gin}] stored : {Action}", resp.Gin, resp.DecisionPoints);

        var responseKey = (resp.Gin, resp.DecisionPoint);
        if (_pendingDecisionResponses.TryRemove(responseKey, out var pendingResponse))
        {
            pendingResponse.TrySetResult(resp);
        }
        else
        {
            Logger.Warning("[{Dev}] Received DecisionResponse for GIN {Gin} at {DP}, but no lifecycle is waiting.",
                Config.Name, resp.Gin, resp.DecisionPoint);
        }

        // Calculate and log the round trip time
        if (_decisionRequestTimestamps.TryRemove(resp.Gin, out long startTimestamp))
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            Tracker.AddProcessTime(elapsedMs);
            Logger.Information("[{Dev}] WCS Response Time for GIN {Gin}: {ElapsedMs:F2} ms", Config.Name,
                resp.Gin, elapsedMs);
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

    private async Task AnnounceScriptedScenarioStartAsync(ScriptedPlcScenario scenario, CancellationToken token)
    {
        var startDelay = TimeSpan.FromMilliseconds(TestStartDelayMs);
        var payload = CreateTestSummaryPayload(scenario, startDelay);
        var message = PlcMessageParser.CreateTestMessage(Key.ElementName, payload);

        await SendAsync(message.ToString(), token);
        Logger.Information(
            "[{Dev}] Sent TEST summary for scripted PLC scenario {Scenario}. Starting in {DelayMs}ms.",
            Config.Name, scenario.Name, TestStartDelayMs);

        await Task.Delay(startDelay, token);
    }

    private TestMessagePayload CreateTestSummaryPayload(
        ScriptedPlcScenario scenario,
        TimeSpan startsIn,
        string eventName = "START",
        string status = "PENDING",
        DateTime? completedAtUtc = null,
        TimeSpan? duration = null,
        int? completedStageCount = null,
        int? completedToteCount = null)
    {
        var now = DateTime.UtcNow;
        var stages = scenario.Stages
            .OrderBy(stage => stage.Stage)
            .Select(CreateTestStageSummary)
            .ToList();

        var properties = scenario.VirtualPlcProperties;

        return new TestMessagePayload(
            TestName: scenario.Name,
            Description: scenario.Description,
            ScriptPath: _scriptedScenarioPath,
            GeneratedAtUtc: now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            StartsAtUtc: now.Add(startsIn).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            StartsInSeconds: (int)startsIn.TotalSeconds,
            StageCount: scenario.Stages.Count,
            ToteCount: stages.Sum(stage => stage.ToteCount),
            DecisionChain: properties?.DecisionPoints,
            Printer1: properties?.Printer1 ?? _printer1,
            Printer2: properties?.Printer2 ?? _printer2,
            Stages: stages)
        {
            Event = eventName,
            Status = status,
            CompletedAtUtc = completedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            DurationSeconds = duration?.TotalSeconds,
            CompletedStageCount = completedStageCount,
            CompletedToteCount = completedToteCount
        };
    }

    private static TestStageSummary CreateTestStageSummary(ScriptedPlcStage stage)
    {
        var totes = stage.ExpandTotes().ToList();
        var expectedOutcomes = totes
            .Select(tote => tote.ExpectedOutcome)
            .Where(outcome => !string.IsNullOrWhiteSpace(outcome))
            .Select(outcome => outcome!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(outcome => outcome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TestStageSummary(
            Stage: stage.Stage,
            Name: stage.Name,
            ToteCount: totes.Count > 0 ? totes.Count : stage.BarcodeCount ?? 0,
            FirstGin: totes.Count > 0 ? totes.First().Gin : null,
            LastGin: totes.Count > 0 ? totes.Last().Gin : null,
            InductionSpacingMs: stage.InductionSpacingMs,
            ExpectedOutcomes: expectedOutcomes,
            ExpectedFlow: stage.ExpectedFlow);
    }

    private async Task AnnounceScriptedScenarioEndAsync(
        ScriptedPlcScenario scenario,
        string status,
        TimeSpan duration,
        int completedStageCount,
        int completedToteCount,
        CancellationToken token)
    {
        var payload = CreateTestSummaryPayload(
            scenario,
            TimeSpan.Zero,
            eventName: "END",
            status: status,
            completedAtUtc: DateTime.UtcNow,
            duration: duration,
            completedStageCount: completedStageCount,
            completedToteCount: completedToteCount);
        var message = PlcMessageParser.CreateTestEndMessage(Key.ElementName, payload);

        try
        {
            await SendAsync(message.ToString(), token);
            Logger.Information(
                "[{Dev}] Sent TESTEND summary for scripted PLC scenario {Scenario}. Status: {Status}, Stages: {Stages}, Totes: {Totes}, DurationSeconds: {Duration:F2}.",
                Config.Name, scenario.Name, status, completedStageCount, completedToteCount, duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            Logger.Warning(
                "[{Dev}] Could not send TESTEND summary for scripted PLC scenario {Scenario}: {Error}",
                Config.Name, scenario.Name, ex.Message);
        }
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
                int gin = Interlocked.Increment(ref _currentGin);

                SimulationCoordinator.UpdateGin(gin);

                await ProcessToteLifecycleInGlobalOrderAsync(gin, decisionPhases, token);

                if (inductIntervalMs > 0)
                {
                    await Task.Delay(inductIntervalMs, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* Clean exit */
        }
    }

    private async Task RunScriptedScenarioAsync(
        ScriptedPlcScenario scenario,
        List<List<DecisionStep>> decisionPhases,
        CancellationToken token)
    {
        var scenarioClock = new Stopwatch();
        var completedStageCount = 0;
        var completedToteCount = 0;

        try
        {
            await AnnounceScriptedScenarioStartAsync(scenario, token);
            scenarioClock.Start();

            Logger.Information("[{Dev}] Starting scripted PLC scenario {Scenario} with {StageCount} stages.",
                Config.Name, scenario.Name, scenario.Stages.Count);

            foreach (var stage in scenario.Stages.OrderBy(s => s.Stage))
            {
                var totes = stage.ExpandTotes().ToList();
                if (totes.Count == 0)
                {
                    Logger.Warning("[{Dev}] Scripted stage {Stage} {Name} has no totes.",
                        Config.Name, stage.Stage, stage.Name);
                    continue;
                }

                Logger.Information("[{Dev}] Starting scripted stage {Stage} {Name} with {Count} totes.",
                    Config.Name, stage.Stage, stage.Name, totes.Count);

                var stageTasks = totes
                    .Select(tote => Task.Run(
                        () => RunScriptedToteAfterDelayAsync(tote, decisionPhases, token),
                        token))
                    .ToArray();

                await Task.WhenAll(stageTasks);

                Logger.Information("[{Dev}] Completed scripted stage {Stage} {Name}.",
                    Config.Name, stage.Stage, stage.Name);

                completedStageCount++;
                completedToteCount += totes.Count;
            }

            scenarioClock.Stop();
            await AnnounceScriptedScenarioEndAsync(
                scenario,
                "COMPLETED",
                scenarioClock.Elapsed,
                completedStageCount,
                completedToteCount,
                token);

            Logger.Information("[{Dev}] Scripted PLC scenario {Scenario} completed.",
                Config.Name, scenario.Name);
        }
        catch (OperationCanceledException)
        {
            scenarioClock.Stop();
            await AnnounceScriptedScenarioEndAsync(
                scenario,
                "CANCELED",
                scenarioClock.Elapsed,
                completedStageCount,
                completedToteCount,
                CancellationToken.None);

            // Normal during shutdown or reconnect.
        }
        catch (Exception ex)
        {
            scenarioClock.Stop();
            Logger.Error(ex, "[{Dev}] Scripted PLC scenario {Scenario} failed.",
                Config.Name, scenario.Name);
            await AnnounceScriptedScenarioEndAsync(
                scenario,
                "FAILED",
                scenarioClock.Elapsed,
                completedStageCount,
                completedToteCount,
                CancellationToken.None);
        }
    }

    private async Task RunScriptedToteAfterDelayAsync(
        ScriptedPlcTote tote,
        List<List<DecisionStep>> phases,
        CancellationToken token)
    {
        try
        {
            if (tote.InductAtMs > 0)
            {
                await Task.Delay(tote.InductAtMs, token);
            }

            if (!string.IsNullOrWhiteSpace(tote.SequenceAnomaly))
            {
                Logger.Warning("[{Dev}] sequence anomaly: {Anomaly}",
                    Config.Name, FormatSequenceAnomaly(tote.SequenceAnomaly));
            }

            SimulationCoordinator.UpdateGin(tote.Gin);
            Logger.Information("[{Dev}] Scripted stage {Stage} inducting GIN {Gin} barcode {Barcode}.",
                Config.Name, tote.Stage, tote.Gin, tote.Barcode);

            await ProcessToteLifecycleAsync(
                tote.Gin,
                phases,
                token,
                tote.Barcode,
                CloneMetadata(tote.InductMetadata));
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown or reconnect.
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Scripted tote failed. Stage {Stage}, GIN {Gin}, Barcode {Barcode}",
                Config.Name, tote.Stage, tote.Gin, tote.Barcode);
        }
        finally
        {
            CancelPendingResponsesForGin(tote.Gin);
        }
    }

    // Define this at the class level for thread-safe round-robin routing
    private int _roundRobinIndex = 0;

    private async Task ProcessToteLifecycleInGlobalOrderAsync(
        int gin,
        List<List<DecisionStep>> phases,
        CancellationToken token)
    {
        await _lifecycleOrderGate.WaitAsync(token);
        try
        {
            await ProcessToteLifecycleAsync(gin, phases, token);
        }
        finally
        {
            CancelPendingResponsesForGin(gin);
            _lifecycleOrderGate.Release();
        }
    }

    private TaskCompletionSource<DecisionResponsePayload> RegisterPendingDecisionResponse(
        int gin,
        string decisionPoint)
    {
        var key = (gin, decisionPoint);
        var pendingResponse =
            new TaskCompletionSource<DecisionResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_pendingDecisionResponses.TryRemove(key, out var existing))
        {
            existing.TrySetCanceled();
        }

        _pendingDecisionResponses[key] = pendingResponse;
        return pendingResponse;
    }

    private async Task<DecisionResponsePayload?> WaitForDecisionResponseAsync(
        int gin,
        string decisionPoint,
        TaskCompletionSource<DecisionResponsePayload> pendingResponse,
        CancellationToken token)
    {
        try
        {
            if (_decisionResponseTimeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(_decisionResponseTimeoutMs);
                return await pendingResponse.Task.WaitAsync(timeoutCts.Token);
            }

            return await pendingResponse.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && _decisionResponseTimeoutMs > 0)
        {
            Logger.Warning(
                "[{Dev}] Timed out waiting {Timeout}ms for DecisionResponse. GIN {Gin}, DP {DP}",
                Config.Name, _decisionResponseTimeoutMs, gin, decisionPoint);
            return null;
        }
        finally
        {
            _pendingDecisionResponses.TryRemove((gin, decisionPoint), out _);
        }
    }

    private void CancelPendingResponsesForGin(int gin)
    {
        foreach (var key in _pendingDecisionResponses.Keys.Where(k => k.Gin == gin))
        {
            if (_pendingDecisionResponses.TryRemove(key, out var pendingResponse))
            {
                pendingResponse.TrySetCanceled();
            }
        }
    }

    private async Task SendDecisionUpdatesAsync(DecisionResponsePayload resp, CancellationToken token)
    {
        if (resp.DecisionPoints == null || resp.DecisionPoints.Count == 0)
        {
            return;
        }

        foreach (var dp in resp.DecisionPoints)
        {
            string? effectiveBarcode = GetEffectiveBarcode(resp.Gin, resp.DecisionPoint);

            string action = dp;
            int reasonCode = 0;

            // Sorter Simulation Mode: Infer from name (not PNA)
            if (!resp.DecisionPoint.Contains("PNA", StringComparison.OrdinalIgnoreCase) &&
                Random.Shared.Next(100) < 5)
            {
                action = "REJECT";
                reasonCode = 99;
            }

            var updateMsg = PlcMessageParser.CreateDecisionUpdate(
                Key.ElementName,
                resp.DecisionPoint,
                resp.Gin,
                action,
                effectiveBarcode != null ? new List<string> { effectiveBarcode } : null,
                reasonCode);

            await SendAsync(updateMsg.ToString(), token);
            Logger.Information(
                "[{Dev}] Sent DecisionUpdate for Gin: {Gin} Action: {Action}",
                Config.Name, resp.Gin, action);
        }
    }


    private async Task ProcessToteLifecycleAsync(
        int gin,
        List<List<DecisionStep>> phases,
        CancellationToken token,
        string? scriptedBarcode = null,
        JsonObject? inductMetadata = null)
    {
        if (phases == null || phases.Count == 0) return;

        var conveyorClock = new Stopwatch();
        string destination = null;
        List<string>? assignedPrinterStations = null;

        try
        {
            for (int i = 0; i < phases.Count; i++)
            {
                Logger.Information("[Beginning of loop : phases {phases}] Gin: {gin} Message: {i}", phases.Count, gin,
                    i);
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
                        string? barcode = scriptedBarcode ?? TryGetScannerBarcode(targetStep.DecisionPoint);

                        if (barcode == null)
                        {
                            // --- SIMULATION OVERRIDES ---
                            if (gin == 20) barcode = "?????";
                            else if (gin == 21) barcode = "****";
                            else if (gin == 22) barcode = "?????";
                            else if (gin == 24) barcode = "*****";
                            else if (_barcodes != null && _barcodes.Count > 0)
                            {
                                int bcIndex = Interlocked.Increment(ref _barcodeIndex) % _barcodes.Count;
                                barcode = _barcodes[Math.Abs(bcIndex)];
                            }
                            else
                            {
                                barcode = $"SIM-{gin:D4}";
                            }
                        }

                        // --- SKIP OVERRIDE ---
                        if (scriptedBarcode == null && gin == 25)
                        {
                            Logger.Warning(
                                "[{Dev}] SIMULATION OVERRIDE: Skipping DecisionRequest (No Label) for GIN {Gin}",
                                Config.Name, gin);
                            return; // Stop lifecycle for this tote
                        }

                        // create the decision request message
                        string? effectiveBarcode = GetEffectiveBarcode(gin, targetStep.DecisionPoint, barcode);
                        var msg = PlcMessageParser.CreateDecisionRequest(Key.ElementName, targetStep.DecisionPoint, gin,
                            effectiveBarcode, CloneMetadata(inductMetadata), _printer1, _printer2);
                        _ginBarcode[gin] = barcode;
                        SimulationCoordinator.RecordBarcode(gin, barcode);
                        var str = msg.ToString();
                        Logger.Information(" Gin: {gin} barcode: {barcode} (Source: {Src})",
                            gin, _ginBarcode[gin], scriptedBarcode != null
                                ? "Scripted"
                                : barcode.StartsWith("SIM-")
                                    ? "Generated"
                                    : "Scanner/List");

                        var pendingResponse = RegisterPendingDecisionResponse(gin, targetStep.DecisionPoint);
                        _decisionRequestTimestamps[gin] = Stopwatch.GetTimestamp();
                        await SendAsync(str, token);

                        var routingResponse =
                            await WaitForDecisionResponseAsync(gin, targetStep.DecisionPoint, pendingResponse, token);
                        if (routingResponse != null)
                        {
                            assignedPrinterStations = routingResponse.DecisionPoints?
                                .Where(action => !string.IsNullOrWhiteSpace(action))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            await SendDecisionUpdatesAsync(routingResponse, token);
                        }

                        break;

                    case 1:
                        if (currentPhaseOptions.Count == 1)
                            Logger.Debug("[PHASE 2 : {Dev}] Gin: {gin} Message: {Msg}", Config.Name, gin,
                                currentPhaseOptions[0].DecisionPoint);

                        var printerPhaseTimings = GetPhaseStepTimings(currentPhaseOptions);
                        int printerPhaseEndDistanceMs = GetPhaseEndDistanceMs(printerPhaseTimings);

                        if (assignedPrinterStations == null || assignedPrinterStations.Count == 0)
                        {
                            Logger.Information(
                                "[PHASE 2 : {Dev}] Gin {gin} has no assigned printer stations from the induct response. Skipping printer-location requests.",
                                Config.Name, gin);
                            await WaitUntilConveyorElapsedAsync(
                                conveyorClock,
                                printerPhaseEndDistanceMs,
                                gin,
                                "PHASE 2 CLEAR",
                                token);
                            break;
                        }

                        Logger.Debug("[PHASE 2 : Assigned printer stations {Printers} for Gin {gin}",
                            string.Join(",", assignedPrinterStations), gin);

                        // 2. The tote has reached the physical divert. Determine the target.
                        if (currentPhaseOptions.Count == 0)
                        {
                            break;
                        }

                        var selectedSteps = assignedPrinterStations
                            .Select((decisionPoint, index) => new
                            {
                                DecisionPoint = decisionPoint,
                                Index = index,
                                Timing = printerPhaseTimings.FirstOrDefault(timing =>
                                    string.Equals(timing.Step.DecisionPoint, decisionPoint,
                                        StringComparison.OrdinalIgnoreCase))
                            })
                            .Where(item => item.Timing != null)
                            .OrderBy(item => item.Timing!.ElapsedMs)
                            .ThenBy(item => item.Index)
                            .ToList();

                        if (selectedSteps.Count == 0)
                        {
                            Logger.Warning("[PHASE 2 : {Dev}] Gin: {gin} has no matching print station for actions {Actions}",
                                Config.Name, gin, string.Join(",", assignedPrinterStations));
                            await WaitUntilConveyorElapsedAsync(
                                conveyorClock,
                                printerPhaseEndDistanceMs,
                                gin,
                                "PHASE 2 CLEAR",
                                token);
                            break;
                        }

                        foreach (var selected in selectedSteps)
                        {
                            var timing = selected.Timing!;
                            targetStep = timing.Step;
                            await WaitUntilConveyorElapsedAsync(
                                conveyorClock,
                                timing.ElapsedMs,
                                gin,
                                "PHASE 2",
                                token);

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

                        await WaitUntilConveyorElapsedAsync(
                            conveyorClock,
                            printerPhaseEndDistanceMs,
                            gin,
                            "PHASE 2 CLEAR",
                            token);

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

    private async Task WaitUntilConveyorElapsedAsync(
        Stopwatch conveyorClock,
        int targetElapsedMs,
        int gin,
        string phaseName,
        CancellationToken token)
    {
        if (targetElapsedMs <= 0)
        {
            return;
        }

        if (!conveyorClock.IsRunning)
        {
            await Task.Delay(targetElapsedMs, token);
            return;
        }

        var remainingTravelMs = targetElapsedMs - (int)conveyorClock.ElapsedMilliseconds;
        if (remainingTravelMs <= 0)
        {
            return;
        }

        Logger.Debug("[{Phase} : {Dev}] Gin: {gin} Waiting for {ms}ms",
            phaseName, Config.Name, gin, remainingTravelMs);

        await Task.Delay(remainingTravelMs, token);
    }

    private static List<PhaseStepTiming> GetPhaseStepTimings(List<DecisionStep> phase)
    {
        var timings = new List<PhaseStepTiming>(phase.Count);
        if (phase.Count == 0)
        {
            return timings;
        }

        // Equal delays are common in the two-printer config and represent repeated travel segments
        // through the printer bank, not multiple stations at the exact same physical point.
        bool useCumulativeTiming = phase.Count > 1 &&
                                   phase.Select(step => step.DistanceMs)
                                       .Distinct()
                                       .Count() == 1;

        var elapsedMs = 0;
        foreach (var step in phase)
        {
            elapsedMs = useCumulativeTiming
                ? elapsedMs + step.DistanceMs
                : step.DistanceMs;
            timings.Add(new PhaseStepTiming(step, elapsedMs));
        }

        return timings;
    }

    private static int GetPhaseEndDistanceMs(List<PhaseStepTiming> phaseTimings)
    {
        return phaseTimings.Count == 0 ? 0 : phaseTimings.Max(timing => timing.ElapsedMs);
    }

    private sealed record PhaseStepTiming(DecisionStep Step, int ElapsedMs);

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
