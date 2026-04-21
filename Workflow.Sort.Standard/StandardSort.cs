using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace Workflow.Sort.Standard;

public class StandardSort : WorkflowBase
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
            // Use Interlocked for thread safety
            int nextIndex = Interlocked.Increment(ref _roundRobinIndex);
            // Ensure positive index
            int index = (nextIndex & int.MaxValue) % _diverts.Count;
            string selectedExit = _diverts[index];

            Logger.Information("[{Workflow}] RoundRobin: GIN {Gin} at {DP} -> Selected Exit: {Exit}", 
                Config.Name, gin, dp, selectedExit);

            var response = new
            {
                DecisionPoint = dp,
                GIN = gin,
                Actions = new List<string> { selectedExit }
            };

            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in RoundRobinSort for GIN {Gin}", Config.Name, envelope.Gin);
            return null;
        }
    }


    public async Task<object?> HandleInductionSqlSort(MessageEnvelope envelope, CancellationToken ct)
    {
        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            if (node == null) return null;

            var dp = node["DecisionPoint"]?.GetValue<string>();
            var gin = node["GIN"]?.GetValue<int>();
            
            // Extract Barcode
            var barcodesArray = node["Barcodes"]?.AsArray();
            string firstBarcode = (barcodesArray != null && barcodesArray.Count > 0)
                ? barcodesArray[0]?.ToString() ?? string.Empty
                : string.Empty;

            if (dp == null || gin == null)
            {
                Logger.Warning("[{Workflow}] Invalid payload for SqlSort: Missing DecisionPoint or GIN.", Config.Name);
                return null;
            }

            string spName = "[SorterAssignment]";

            Logger.Information("[{Workflow}] SQL Sort SP Request: {SP} for Sorter: {Sorter}, Barcode: {BC}, GIN: {Gin}", 
                Config.Name, spName, Config.Name, firstBarcode, gin);

            var sqlRequest = new
            {
                Sql = spName,
                Operation = "QUERY",
                CommandType = nameof(CommandType.StoredProcedure),
                Parameters = new Dictionary<string, object>
                {
                    { "SorterName",dp  },
                    { "barcode", firstBarcode },
                    { "GIN", gin }
                },
                DecisionPoint = dp,
                GIN = gin
            };

            return JsonSerializer.Serialize(sqlRequest);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in HandleInductionSqlSort for GIN {Gin}", Config.Name, envelope.Gin);
            return null;
        }
    }


    public async Task<object?> HandleDestinastionResult(MessageEnvelope envelope, CancellationToken ct)
    {
        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            if (node == null) return null;

            var results = node["Results"]?.AsArray();
            var dp = node["DecisionPoint"]?.GetValue<string>();
            var gin = node["GIN"]?.GetValue<int>();

            if (dp == null || gin == null)
            {
                Logger.Warning("[{Workflow}] Invalid Database response: Missing DecisionPoint or GIN.", Config.Name);
                return null;
            }

            List<string> actions = new();
            if (results != null && results.Count > 0)
            {
                foreach (var row in results)
                {
                    // Dapper results as dynamic/object often serialize as JSON objects with properties
                    var exit = row?["ExitName"]?.GetValue<string>();
                    if (exit != null) actions.Add(exit);
                }
            }

            if (actions.Count == 0)
            {
                Logger.Warning("[{Workflow}] No exits found in DB for GIN {Gin}. Defaulting to REJECT.", Config.Name, gin);
                actions.Add("REJECT");
            }

            Logger.Information("[{Workflow}] SQL Result: GIN {Gin} at {DP} -> Actions: {Actions}", 
                Config.Name, gin, dp, string.Join(", ", actions));

            var response = new
            {
                DecisionPoint = dp,
                GIN = gin,
                Actions = actions
            };

            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error in HandleDestinastionResult", Config.Name);
            return null;
        }
    }
}
