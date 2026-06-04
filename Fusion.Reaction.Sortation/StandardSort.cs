using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Device.Plc.Suite.Messages;
using Serilog;
using System.Data;

namespace Workflow.Sort.Standard;

public class StandardSort : ReactionBase
{
    private int _roundRobinIndex = 0;
    private readonly List<string> _diverts = new();

    public StandardSort(IMessageBus bus, WorkflowConfig config, ILogger logger)
        : base(bus, config, logger)
    {
        string? rawDiverts = ConfigurationLoader.GetOptionalConfig(config.Properties, "Diverts", string.Empty);
        if (!string.IsNullOrWhiteSpace(rawDiverts))
        {
            _diverts = rawDiverts.Split(';', StringSplitOptions.RemoveEmptyEntries)
                             .Select(e => e.Trim())
                             .ToList();
        }

        Logger.Information("[{Workflow}] Initialized with {Count} exits: {Exits}", 
            Config.Name, _diverts.Count, string.Join(", ", _diverts));
    }

    public async Task<object?> HandleInductionRoundRobinSort(MessageEnvelope envelope, CancellationToken ct)
    {
        if (_diverts.Count == 0)
        {
            Logger.Warning("[{Workflow}] No Diverts configured for RoundRobinSort.", Config.Name);
            return null;
        }

        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            if (node == null) return null;

            var dp = node["DecisionPoint"]?.GetValue<string>();
            var gin = node["GIN"]?.GetValue<int>();

            if (dp == null || gin == null)
            {
                Logger.Warning("[{Workflow}] Invalid payload for RoundRobinSort: Missing DecisionPoint or GIN.", Config.Name);
                return null;
            }

            // Perform Round Robin
            int nextIndex = Interlocked.Increment(ref _roundRobinIndex);
            int index = (nextIndex & int.MaxValue) % _diverts.Count;
            string selectedExit = _diverts[index];

            Logger.Information("[{Workflow}] RoundRobin: GIN {Gin} at {DP} -> Selected Exit: {Exit}", 
                Config.Name, gin, dp, selectedExit);

            return new DecisionResponsePayload(dp, gin.Value, [selectedExit]);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in RoundRobinSort for GIN {Gin}", Config.Name, envelope.Gin);
            return null;
        }
    }


    /// <summary>
    /// ASYNCHRONOUS: Creates a SQL request to be handled by the DatabaseElementManager.
    /// </summary>
    public async Task<object?> HandleInductionSqlSort(MessageEnvelope envelope, CancellationToken ct)
    {
        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            if (node == null || node is not JsonObject obj) return null;

            // Helper to get property case-insensitively
            JsonNode? GetProp(JsonObject o, string key) => 
                o.FirstOrDefault(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

            var dp = GetProp(obj, "DecisionPoint")?.GetValue<string>();
            var gin = GetProp(obj, "GIN")?.GetValue<int>();
            
            // Extract Barcode - Check both Barcodes and barcodes
            var barcodesArray = GetProp(obj, "Barcodes")?.AsArray() ?? GetProp(obj, "barcodes")?.AsArray();
            string firstBarcode = (barcodesArray != null && barcodesArray.Count > 0)
                ? barcodesArray[0]?.GetValue<string>() ?? string.Empty
                : string.Empty;

            if (dp == null || gin == null)
            {
                Logger.Warning("[{Workflow}] Invalid payload for SqlSort: Missing DecisionPoint or GIN. Payload: {Payload}", Config.Name, payloadStr);
                return null;
            }

            string spName = "[usp_GetSorterAssignment]";

            Logger.Information("[{Workflow}] SQL Sort SP Request: {SP} for Barcode: {BC}, GIN: {Gin}", 
                Config.Name, spName, firstBarcode, gin);

            var sqlRequest = new
            {
                Sql = spName,
                Operation = "QUERY",
                CommandType = nameof(CommandType.StoredProcedure),
                Parameters = new Dictionary<string, object>
                {
                    { "sorterid", 1 },
                    { "barcode", firstBarcode },
                    { "gin", gin }
                },
                DecisionPoint = dp,
                GIN = gin
            };

            return JsonSerializer.Serialize(sqlRequest);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in HandleInductionSqlSort for GIN {Gin}. Payload: {Payload}", Config.Name, envelope.Gin, payloadStr);
            return null;
        }
    }


    public async Task<object?> HandleDestinationResult(MessageEnvelope envelope, CancellationToken ct)
    {
        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            if (node == null || node is not JsonObject topObj) return null;

            // Helper to get property case-insensitively
            JsonNode? GetProp(JsonObject obj, string key) => 
                obj.FirstOrDefault(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

            var results = GetProp(topObj, "Results")?.AsArray();
            var dp = GetProp(topObj, "DecisionPoint")?.GetValue<string>();
            var ginNode = GetProp(topObj, "GIN");
            var gin = ginNode?.GetValue<int>();

            if (dp == null || gin == null)
            {
                Logger.Warning("[{Workflow}] Invalid Database response: Missing DecisionPoint or GIN. Payload: {Payload}", Config.Name, payloadStr);
                return null;
            }

            List<string> actions = new();
            if (results != null && results.Count > 0)
            {
                foreach (var rowNode in results)
                {
                    if (rowNode == null) continue;
                    if (rowNode is not JsonObject row)
                    {
                        Logger.Warning("[{Workflow}] Unexpected result format in SQL row: {Kind}. Row Data: {Data}", 
                            Config.Name, rowNode.GetValueKind(), rowNode.ToJsonString());
                        continue;
                    }

                    // Check for multiple possible column names (Case-Insensitive lookup)
                    string? exit = null;
                    var keys = row.Select(kvp => kvp.Key).ToList();
                    var targetKeys = new[] 
                    { 
                        "Result", "ExitName", "Assignment", "Exit", "TargetLane", "LaneId", "Lane", "@TargetLane", 
                        "Target_Lane", "Dest", "Destination", "DestLane", "Dest_Lane", "Target", "Exit_Lane", "ExitLane",
                        "SortedLane", "AssignedLane", "Target_Exit", "TargetExit", "Lane_No", "LaneNo", "Exit_No", "ExitNo",
                        "Destination_Lane", "DestinationLane"
                    };
                    
                    // 1. Try named lookup (Case-insensitive + Trimmed)
                    var matchingKey = keys.FirstOrDefault(k => targetKeys.Contains(k.Trim(), StringComparer.OrdinalIgnoreCase));
                    
                    if (matchingKey != null)
                    {
                        var exitNode = row[matchingKey];
                        if (exitNode != null)
                        {
                            exit = exitNode.GetValueKind() == JsonValueKind.String 
                                ? exitNode.GetValue<string>() 
                                : exitNode.ToString().Trim('"');
                        }
                    }
                    
                    // 2. Fallback: If no recognized name, but there is at least one column, take the FIRST column
                    if (string.IsNullOrWhiteSpace(exit) && keys.Count > 0)
                    {
                        var firstKey = keys[0];
                        var firstNode = row[firstKey];
                        if (firstNode != null)
                        {
                            exit = firstNode.GetValueKind() == JsonValueKind.String 
                                ? firstNode.GetValue<string>() 
                                : firstNode.ToString().Trim('"');
                            
                            Logger.Information("[{Workflow}] No named column matched. Falling back to first column '{Key}': {Value}", 
                                Config.Name, firstKey, exit);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(exit))
                    {
                        actions.Add(exit);
                    }
                    else
                    {
                        Logger.Warning("[{Workflow}] SQL Row found but could not extract exit. GIN: {Gin}, Available Keys: {Keys}, Row: {Row}", 
                            Config.Name, gin, string.Join(", ", keys), row.ToJsonString());
                    }
                }
            }
            if (actions.Count == 0)
            {
                Logger.Warning("[{Workflow}] No exits found in DB for GIN {Gin}. Results Count: {Count}. Payload: {Payload}", 
                    Config.Name, gin, results?.Count ?? 0, payloadStr);
                actions.Add("15");
            }

            Logger.Information("[{Workflow}] SQL Async Result: GIN {Gin} at {DP} -> Actions: {Actions}", 
                Config.Name, gin, dp, string.Join(", ", actions));

            return new DecisionResponsePayload(dp, gin.Value, actions);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in HandleDestinationResult. Payload: {Payload}", Config.Name, payloadStr);
            return null;
        }
    }
}
