using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog.Core;

namespace Fusion.Element.Support.CLI;

public class SimulationValidationElement : ElementBase<SimulationValidationElement.State, SimulationValidationElement.Event, ElementMetric>
{
    public enum State { Idle, Monitoring, Validating, Finished }
    public enum Event { Start, Stop, Validate, Reset }

    private readonly ConcurrentDictionary<string, IElementStatus> _latestStatuses = new();
    private readonly ConcurrentDictionary<string, int> _initialResourceCounts = new();
    private readonly ConcurrentDictionary<string, ActiveTestRun> _activeTestRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _testResultsLock = new();
    private readonly object _subscriptionLock = new();
    private readonly Func<MessageEnvelope, CancellationToken, Task> _statusUpdateHandler;
    private readonly Func<MessageEnvelope, CancellationToken, Task> _anyBusMessageHandler;
    private readonly string _testResultsPath;
    private bool _subscriptionsActive;

    public SimulationValidationElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, ls, State.Idle, Event.Start)
    {
        _testResultsPath = ResolveTestResultsPath(config);
        _statusUpdateHandler = HandleStatusUpdateAsync;
        _anyBusMessageHandler = HandleAnyBusMessageAsync;

        ConfigureStateMachine();
        SubscribeToValidationSources();
    }

    private void SubscribeToValidationSources()
    {
        lock (_subscriptionLock)
        {
            if (_subscriptionsActive)
            {
                return;
            }

            SimulationCoordinator.PhaseChanged += OnSimulationPhaseChanged;
            _ = MessageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), _statusUpdateHandler);
            _ = MessageBus.SubscribeAsync("#", _anyBusMessageHandler);
            _subscriptionsActive = true;
        }
    }

    private void UnsubscribeFromValidationSources()
    {
        lock (_subscriptionLock)
        {
            if (!_subscriptionsActive)
            {
                return;
            }

            SimulationCoordinator.PhaseChanged -= OnSimulationPhaseChanged;
            MessageBus.Unsubscribe(MessageBusTopic.ElementStatus.ToString(), _statusUpdateHandler);
            MessageBus.Unsubscribe("#", _anyBusMessageHandler);
            _subscriptionsActive = false;
        }
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Idle)
            .Permit(Event.Start, State.Monitoring);

        Machine.Configure(State.Monitoring)
            .OnEntry(() => Logger.Information("[{Dev}] Simulation monitoring started.", Config.Name))
            .Permit(Event.Validate, State.Validating)
            .Permit(Event.Stop, State.Idle);

        Machine.Configure(State.Validating)
            .OnEntry(PerformValidation)
            .Permit(Event.Reset, State.Idle);
    }

    private Task HandleStatusUpdateAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is IElementStatus status)
        {
            string name = status.ElementId.ElementName;
            _latestStatuses[name] = status;

            // Capture baseline resources if this is the first time we see this element
            if (SimulationCoordinator.CurrentPhase == SimulationPhase.SteadyState)
            {
                _initialResourceCounts.TryAdd(name, status.ResourceDeepCount);
            }
        }
        return Task.CompletedTask;
    }

    private Task HandleAnyBusMessageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope == null)
        {
            return Task.CompletedTask;
        }

        if (TryReadTestDefinition(envelope, out var definition))
        {
            HandleTestControlMessage(definition, envelope.Created);
            return Task.CompletedTask;
        }

        foreach (var run in _activeTestRuns.Values)
        {
            run.Record(envelope);
        }

        return Task.CompletedTask;
    }

    private void HandleTestControlMessage(TestRunDefinition definition, DateTime envelopeCreatedUtc)
    {
        var eventName = string.IsNullOrWhiteSpace(definition.Event)
            ? "START"
            : definition.Event.Trim().ToUpperInvariant();
        var testName = string.IsNullOrWhiteSpace(definition.TestName) ? "TEST" : definition.TestName;

        if (eventName == "END")
        {
            if (!_activeTestRuns.TryRemove(testName, out var run))
            {
                run = new ActiveTestRun(definition, envelopeCreatedUtc);
            }

            run.End = definition;
            run.EndedUtc = ParseUtc(definition.CompletedAtUtc) ?? envelopeCreatedUtc;
            WriteTestResult(run);
            return;
        }

        _activeTestRuns[testName] = new ActiveTestRun(definition, envelopeCreatedUtc);
        Logger.Information("[{Dev}] Tracking test run {TestName}; results will be written to {Path}",
            Config.Name, testName, _testResultsPath);
    }

    private void OnSimulationPhaseChanged(SimulationPhase phase)
    {
        if (phase == SimulationPhase.Completed)
        {
            Machine.Fire(Event.Validate);
        }
    }

    private void PerformValidation()
    {
        Logger.Information("[{Dev}] === SIMULATION VALIDATION RESULTS ===", Config.Name);

        var report = new StringBuilder();
        int totalErrors = 0;
        int leakCount = 0;

        foreach (var kvp in _latestStatuses)
        {
            var name = kvp.Key;
            var status = kvp.Value;

            // 1. Check Metrics
            if (status.CountError > 0)
            {
                totalErrors += status.CountError;
                Logger.Warning("[{Dev}] Element {Name} reported {Count} errors.", Config.Name, name, status.CountError);
            }

            // 2. Check for Memory Leaks (ResourceDeepCount)
            if (_initialResourceCounts.TryGetValue(name, out int initialCount))
            {
                int currentCount = status.ResourceDeepCount;
                int growth = currentCount - initialCount;

                if (growth > 0)
                {
                    leakCount++;
                    Logger.Error("[{Dev}] POTENTIAL LEAK DETECTED in {Name}: Growth of {Growth} items ({Initial} -> {Current})",
                        Config.Name, name, growth, initialCount, currentCount);
                }
            }

            // 3. Summarize key metrics
            Logger.Information("[{Dev}] {Name,-15} | IN: {In,4} | OUT: {Out,4} | ERR: {Err,2} | RES: {Res,5}",
                Config.Name, name, status.CountInbound, status.CountOutbound, status.CountError, status.ResourceDeepCount);
        }

        Logger.Information("[{Dev}] Validation Summary: {Errors} Total Errors, {Leaks} Potential Leaks Found.",
            Config.Name, totalErrors, leakCount);
        
        Logger.Information("[{Dev}] === END OF VALIDATION ===", Config.Name);
    }

    private void WriteTestResult(ActiveTestRun run)
    {
        try
        {
            var directory = Path.GetDirectoryName(_testResultsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool appendSeparator = File.Exists(_testResultsPath) && new FileInfo(_testResultsPath).Length > 0;
            var report = new StringBuilder();

            if (!appendSeparator)
            {
                report.AppendLine("# Test Results");
                report.AppendLine();
            }
            else
            {
                report.AppendLine();
                report.AppendLine("---");
                report.AppendLine();
            }

            AppendMarkdown(run, report);

            lock (_testResultsLock)
            {
                File.AppendAllText(_testResultsPath, report.ToString());
            }

            Logger.Information("[{Dev}] Test results written to {Path}", Config.Name, _testResultsPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to write test results to {Path}", Config.Name, _testResultsPath);
        }
    }

    private static void AppendMarkdown(ActiveTestRun run, StringBuilder report)
    {
        var test = run.Start;
        var end = run.End;
        var endTime = run.EndedUtc ?? DateTime.UtcNow;
        var duration = end?.DurationSeconds ?? (endTime - run.StartedUtc).TotalSeconds;
        var status = string.IsNullOrWhiteSpace(end?.Status) ? "Complete" : end!.Status;

        report.AppendLine($"## {test.TestName}");
        report.AppendLine();
        report.AppendLine($"- Status: `{status}`");
        if (!string.IsNullOrWhiteSpace(test.Description))
        {
            report.AppendLine($"- Description: {test.Description}");
        }

        if (!string.IsNullOrWhiteSpace(test.ScriptPath))
        {
            report.AppendLine($"- Script: `{test.ScriptPath}`");
        }

        report.AppendLine($"- Run window UTC: `{run.StartedUtc:yyyy-MM-dd HH:mm:ss}` to `{endTime:yyyy-MM-dd HH:mm:ss}`");
        report.AppendLine($"- Run window local: `{run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}` to `{endTime.ToLocalTime():yyyy-MM-dd HH:mm:ss}`");
        report.AppendLine($"- Stages: `{test.StageCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}`");
        report.AppendLine($"- Totes: `{test.ToteCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}`");
        if (!string.IsNullOrWhiteSpace(test.DecisionChain))
        {
            report.AppendLine($"- Decision chain: `{test.DecisionChain}`");
        }

        if (!string.IsNullOrWhiteSpace(test.Printer1) || !string.IsNullOrWhiteSpace(test.Printer2))
        {
            report.AppendLine($"- Printers: `{test.Printer1 ?? "none"}`, `{test.Printer2 ?? "none"}`");
        }

        if (end != null)
        {
            report.AppendLine(
                $"- End message: `{endTime:yyyy-MM-dd HH:mm:ss}` - Status `{status}`, Completed stages `{end.CompletedStageCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}`, Completed totes `{end.CompletedToteCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}`, Duration `{duration.ToString("F2", CultureInfo.InvariantCulture)}s`");
        }

        report.AppendLine($"- Bus messages observed: `{run.TotalMessages}`");
        report.AppendLine($"- Unique GINs observed: `{run.UniqueGins.Count}`");
        report.AppendLine(
            $"- Validation queue readback: `{run.ValidValidationQueueMessages}` valid / `{run.InvalidValidationQueueMessages}` invalid / `{run.ValidationQueueMessages}` total");

        AppendCountBlock(report, "Messages by type", run.MessageTypeCounts);
        AppendCountBlock(report, "Messages by decision point", run.DecisionPointCounts);
        AppendCountBlock(report, "Responses by action", run.ActionCounts);
        AppendCountBlock(report, "Validation queue errors", run.ValidationQueueErrorCounts);

        if (test.Stages.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("### Stage Summary");
            report.AppendLine();
            report.AppendLine("| Stage | Name | Expected totes | GIN range | Expected flow | Expected outcomes |");
            report.AppendLine("| ---: | --- | ---: | --- | --- | --- |");

            foreach (var stage in test.Stages.OrderBy(s => s.Stage))
            {
                string range = stage.FirstGin.HasValue || stage.LastGin.HasValue
                    ? $"{stage.FirstGin?.ToString(CultureInfo.InvariantCulture) ?? "?"}-{stage.LastGin?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                    : "";
                string outcomes = stage.ExpectedOutcomes.Count == 0
                    ? ""
                    : string.Join(", ", stage.ExpectedOutcomes.Select(o => $"`{o}`"));

                report.AppendLine(
                    $"| {stage.Stage} | `{EscapeMarkdownCell(stage.Name)}` | {stage.ToteCount} | {range} | {EscapeMarkdownCell(stage.ExpectedFlow ?? "")} | {EscapeMarkdownCell(outcomes)} |");
            }
        }
    }

    private static string EscapeMarkdownCell(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static void AppendCountBlock(
        StringBuilder report,
        string title,
        ConcurrentDictionary<string, int> counts)
    {
        if (counts.IsEmpty)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine($"### {title}");
        report.AppendLine();

        foreach (var item in counts.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            report.AppendLine($"- `{item.Key}`: `{item.Value}`");
        }
    }

    private static bool IsValidationQueueReadback(MessageEnvelope envelope)
    {
        return string.Equals(envelope.Destination.ElementName, "SIM_VALIDATOR", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(envelope.Destination.MessageType, "LABELVERIFY", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(envelope.Destination.Discriminator, "VALIDATIONQUEUE", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ValidateLabelVerifyMessage(JsonObject? root)
    {
        var errors = new List<string>();
        if (root == null)
        {
            errors.Add("payload is not a JSON object");
            return errors;
        }

        var messageType = RequireString(root, "type", "type", errors);
        if (!string.IsNullOrWhiteSpace(messageType) &&
            !string.Equals(messageType, "LabelVerify", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("type must be LabelVerify");
        }

        var sessionId = RequireString(root, "sessionId", "sessionId", errors);
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            (!Guid.TryParse(sessionId, out var parsedSessionId) || parsedSessionId == Guid.Empty))
        {
            errors.Add("sessionId must be a non-empty GUID");
        }

        RequireString(root, "controllerId", "controllerId", errors);
        RequireString(root, "lineId", "lineId", errors);

        var barcodes = ReadStringArray(root, "barcodes", errors);
        var verificationNode = GetProperty(root, "verifications");
        if (verificationNode is not JsonArray verifications)
        {
            errors.Add("verifications must be an array");
            return errors;
        }

        if (verifications.Count == 0)
        {
            errors.Add("verifications must contain at least one entry");
            return errors;
        }

        for (int i = 0; i < verifications.Count; i++)
        {
            if (verifications[i] is not JsonObject verification)
            {
                errors.Add($"verifications[{i}] must be an object");
                continue;
            }

            RequireString(verification, "applicatorType", $"verifications[{i}].applicatorType", errors);
            var reportedScan = RequireString(verification, "reportedScan", $"verifications[{i}].reportedScan", errors);
            RequireString(verification, "verifyResult", $"verifications[{i}].verifyResult", errors);

            if (!string.IsNullOrWhiteSpace(reportedScan) &&
                barcodes.Count > 0 &&
                !barcodes.Contains(reportedScan, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"verifications[{i}].reportedScan is not listed in barcodes");
            }
        }

        return errors;
    }

    private static string? RequireString(JsonObject root, string propertyName, string displayName, List<string> errors)
    {
        var value = TryGetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{displayName} is required");
        }

        return value;
    }

    private static List<string> ReadStringArray(JsonObject root, string propertyName, List<string> errors)
    {
        if (GetProperty(root, propertyName) is not JsonArray array)
        {
            errors.Add($"{propertyName} must be an array");
            return [];
        }

        var values = array
            .Select(item => item?.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();

        if (values.Count == 0)
        {
            errors.Add($"{propertyName} must contain at least one value");
        }

        return values;
    }

    private static bool TryReadTestDefinition(MessageEnvelope envelope, out TestRunDefinition definition)
    {
        definition = TestRunDefinition.Empty;

        if (!string.Equals(envelope.Destination.MessageType, "TEST", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(envelope.Destination.MessageType, "TESTEND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = PayloadToJsonObject(envelope.Payload);
        if (root == null)
        {
            return false;
        }

        definition = ParseTestDefinition(root);
        return true;
    }

    private static TestRunDefinition ParseTestDefinition(JsonObject root)
    {
        var stages = new List<TestStageDefinition>();
        if (GetProperty(root, "Stages") is JsonArray stageArray)
        {
            foreach (var stageNode in stageArray)
            {
                if (stageNode is not JsonObject stageObject)
                {
                    continue;
                }

                stages.Add(new TestStageDefinition(
                    TryGetInt(stageObject, "Stage") ?? 0,
                    TryGetString(stageObject, "Name") ?? "",
                    TryGetInt(stageObject, "ToteCount") ?? 0,
                    TryGetInt(stageObject, "FirstGin"),
                    TryGetInt(stageObject, "LastGin"),
                    TryGetInt(stageObject, "InductionSpacingMs"),
                    TryGetString(stageObject, "ExpectedFlow"),
                    TryGetStringList(stageObject, "ExpectedOutcomes")));
            }
        }

        return new TestRunDefinition(
            TryGetString(root, "TestName") ?? "TEST",
            TryGetString(root, "Event") ?? "START",
            TryGetString(root, "Status") ?? "PENDING",
            TryGetString(root, "Description"),
            TryGetString(root, "ScriptPath"),
            TryGetString(root, "GeneratedAtUtc"),
            TryGetString(root, "StartsAtUtc"),
            TryGetInt(root, "StartsInSeconds"),
            TryGetString(root, "CompletedAtUtc"),
            TryGetDouble(root, "DurationSeconds"),
            TryGetInt(root, "StageCount"),
            TryGetInt(root, "ToteCount"),
            TryGetInt(root, "CompletedStageCount"),
            TryGetInt(root, "CompletedToteCount"),
            TryGetString(root, "DecisionChain"),
            TryGetString(root, "Printer1"),
            TryGetString(root, "Printer2"),
            stages);
    }

    private static JsonObject? PayloadToJsonObject(object? payload)
    {
        if (payload == null)
        {
            return null;
        }

        try
        {
            if (payload is JsonObject jsonObject)
            {
                return jsonObject;
            }

            if (payload is JsonElement element)
            {
                return JsonNode.Parse(element.GetRawText()) as JsonObject;
            }

            if (payload is string text)
            {
                var jsonText = ExtractJsonObject(text);
                return string.IsNullOrWhiteSpace(jsonText)
                    ? null
                    : JsonNode.Parse(jsonText) as JsonObject;
            }

            return JsonNode.Parse(JsonSerializer.Serialize(payload)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    private static JsonNode? GetProperty(JsonObject obj, string name)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonObject obj, string name)
    {
        var node = GetProperty(obj, name);
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToString();
        }
    }

    private static int? TryGetInt(JsonObject obj, string name)
    {
        var value = TryGetString(obj, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? TryGetDouble(JsonObject obj, string name)
    {
        var value = TryGetString(obj, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static List<string> TryGetStringList(JsonObject obj, string name)
    {
        var node = GetProperty(obj, name);
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => item?.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTime? ParseUtc(string? value)
    {
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string ResolveTestResultsPath(IElementBlueprint config)
    {
        string? configuredPath = config.Properties.TryGetValue("TestResultsPath", out var pathObj)
            ? pathObj?.ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(FindRepositoryRoot(), configuredPath));
        }

        return Path.Combine(FindRepositoryRoot(), "TestResults.md");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FortnaFire.sln")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    public override Task StartAsync(CancellationToken token)
    {
        SubscribeToValidationSources();
        if (Machine.CanFire(Event.Start))
        {
            Machine.Fire(Event.Start);
        }

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken token)
    {
        if (Machine.CanFire(Event.Stop))
        {
            Machine.Fire(Event.Stop);
        }

        UnsubscribeFromValidationSources();
        return Task.CompletedTask;
    }

    protected override ElementHealth MapStateToHealth(State state) => ElementHealth.Normal;

    private sealed class ActiveTestRun
    {
        public ActiveTestRun(TestRunDefinition start, DateTime startedUtc)
        {
            Start = start;
            StartedUtc = ParseUtc(start.GeneratedAtUtc) ?? startedUtc;
        }

        public TestRunDefinition Start { get; }
        public TestRunDefinition? End { get; set; }
        public DateTime StartedUtc { get; }
        public DateTime? EndedUtc { get; set; }
        public int TotalMessages;
        public ConcurrentDictionary<string, int> MessageTypeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> DecisionPointCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> ActionCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> ValidationQueueErrorCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<int, byte> UniqueGins { get; } = new();
        public int ValidationQueueMessages;
        public int ValidValidationQueueMessages;
        public int InvalidValidationQueueMessages;

        public void Record(MessageEnvelope envelope)
        {
            var messageType = envelope.Destination.MessageType;
            if (ShouldIgnoreMessageType(messageType))
            {
                return;
            }

            Interlocked.Increment(ref TotalMessages);
            Increment(MessageTypeCounts, NormalizeMessageType(messageType));

            var root = PayloadToJsonObject(envelope.Payload);
            RecordValidationQueueReadback(envelope, root);

            var decisionPoint = envelope.Destination.Discriminator;
            if (string.IsNullOrWhiteSpace(decisionPoint) && root != null)
            {
                decisionPoint = TryGetString(root, "DecisionPoint");
            }

            Increment(DecisionPointCounts, decisionPoint);

            var gin = envelope.Gin;
            if (gin <= 0 && root != null)
            {
                gin = TryGetInt(root, "GIN") ?? TryGetInt(root, "Gin") ?? 0;
            }

            if (gin > 0)
            {
                UniqueGins.TryAdd(gin, 0);
            }

            if (root != null)
            {
                RecordActionValues(root, "Actions");
                RecordActionValues(root, "DecisionPoints");
            }
        }

        private void RecordValidationQueueReadback(MessageEnvelope envelope, JsonObject? root)
        {
            if (!IsValidationQueueReadback(envelope))
            {
                return;
            }

            Interlocked.Increment(ref ValidationQueueMessages);

            var errors = ValidateLabelVerifyMessage(root);
            if (errors.Count == 0)
            {
                Interlocked.Increment(ref ValidValidationQueueMessages);
                return;
            }

            Interlocked.Increment(ref InvalidValidationQueueMessages);
            foreach (var error in errors)
            {
                Increment(ValidationQueueErrorCounts, error);
            }
        }

        private void RecordActionValues(JsonObject root, string propertyName)
        {
            var node = GetProperty(root, propertyName);
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    Increment(ActionCounts, item?.ToString());
                }

                return;
            }

            Increment(ActionCounts, node?.ToString());
        }

        private static bool ShouldIgnoreMessageType(string? messageType)
        {
            return string.IsNullOrWhiteSpace(messageType) ||
                   string.Equals(messageType, "TEST", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(messageType, "TESTEND", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(messageType, "StatusMessage", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(messageType, "DiagDiscovery", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMessageType(string messageType)
        {
            return string.Equals(messageType, "Update", StringComparison.OrdinalIgnoreCase)
                ? "DUM"
                : messageType;
        }

        private static void Increment(ConcurrentDictionary<string, int> counts, string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            counts.AddOrUpdate(key.Trim(), 1, (_, count) => count + 1);
        }
    }

    private sealed record TestRunDefinition(
        string TestName,
        string Event,
        string Status,
        string? Description,
        string? ScriptPath,
        string? GeneratedAtUtc,
        string? StartsAtUtc,
        int? StartsInSeconds,
        string? CompletedAtUtc,
        double? DurationSeconds,
        int? StageCount,
        int? ToteCount,
        int? CompletedStageCount,
        int? CompletedToteCount,
        string? DecisionChain,
        string? Printer1,
        string? Printer2,
        List<TestStageDefinition> Stages)
    {
        public static TestRunDefinition Empty { get; } = new(
            "TEST",
            "START",
            "PENDING",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);
    }

    private sealed record TestStageDefinition(
        int Stage,
        string Name,
        int ToteCount,
        int? FirstGin,
        int? LastGin,
        int? InductionSpacingMs,
        string? ExpectedFlow,
        List<string> ExpectedOutcomes);
}
