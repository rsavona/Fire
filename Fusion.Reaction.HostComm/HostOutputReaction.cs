using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog;

namespace Workflow.Tura.HostComm;

public class TuraHostOutputReaction : ReactionBase
{
    private readonly string _dbDevice;
    private readonly string _targetHostDevice;
    private readonly int _pollingIntervalMs;

    public TuraHostOutputReaction(IMessageBus bus, ReactionBlueprint config, ILogger logger)
        : base(bus, config, logger)
    {
        _dbDevice = config.Properties.TryGetValue("DbDevice", out var db) ? db.ToString() ?? "" : "";
        _targetHostDevice = config.Properties.TryGetValue("TargetHostDevice", out var host) ? host.ToString() ?? "" : "";
        _pollingIntervalMs = config.Properties.TryGetValue("PollingIntervalMs", out var interval) ? Convert.ToInt32(interval) : 1000;

        Logger.Information("[{Workflow}] Tura Host Output Workflow Initialized. DB: {DB}, Host: {Host}, Polling: {Interval}ms", 
            Config.Name, _dbDevice, _targetHostDevice, _pollingIntervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // Ensure routes are initialized before starting the loop
        await InitializeRoutesAsync();
        UpdateStatus(WorkflowState.Active, WorkflowEvent.Started, DeviceHealth.Normal, "Workflow Active.");

        Logger.Information("[{Workflow}] Starting MessageQueue polling loop...", Config.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollingIntervalMs, stoppingToken);

                if (IsSystemStarted && !string.IsNullOrEmpty(_dbDevice))
                {
                    // Request messages from the MessageQueue table
                    // Use a discriminator in the topic so the response comes back on TURA_DB.QueryResult.PollOutputQueue
                    var pollRequest = new
                    {
                        Sql = "SELECT TOP 10 Id, RawData FROM MessageQueue WHERE DeviceName = @DeviceName AND Status = 0 ORDER BY Id",
                        Operation = "QUERY",
                        Parameters = new Dictionary<string, object>
                        {
                            { "DeviceName", _targetHostDevice }
                        },
                        DecisionPoint = "PollOutputQueue"
                    };

                    var topic = $"{_dbDevice}.Query.PollOutputQueue";
                    await MessageBus.PublishAsync(topic, 
                        new MessageEnvelope(topic, JsonSerializer.Serialize(pollRequest)), stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Workflow}] Error in polling loop", Config.Name);
            }
        }
    }

    /// <summary>
    /// Handles the query results from the database.
    /// </summary>
    public async Task<object?> HandlePollResults(MessageEnvelope envelope, CancellationToken ct)
    {
        string payloadStr = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadStr)) return null;

        try
        {
            var node = JsonNode.Parse(payloadStr);
            var results = node?["Results"]?.AsArray();

            if (results == null || results.Count == 0) return null;

            foreach (var row in results)
            {
                var id = row?["Id"]?.GetValue<long>();
                var rawPayload = row?["RawData"]?.GetValue<string>();

                if (string.IsNullOrEmpty(rawPayload)) continue;

                Logger.Information("[{Workflow}] Sending message from queue (ID: {Id}) to {Target}: {Payload}", 
                    Config.Name, id, _targetHostDevice, rawPayload);

                // Send to Host TCP Server (with CR). 
                // The 'rawPayload' here is the pipe-delimited string (e.g., "BC|CSCN|...") 
                // exactly as the host expects.
                var hostEnvelope = new MessageEnvelope($"{_targetHostDevice}.Outbound.Queue", rawPayload + "\r\n");
                await MessageBus.PublishAsync($"{_targetHostDevice}.Outbound.Queue", hostEnvelope, ct);

                // Mark as processed in the DB
                var updateRequest = new
                {
                    Sql = "UPDATE MessageQueue SET Status = 2  WHERE Id = @Id",
                    Operation = "EXECUTE",
                    Parameters = new Dictionary<string, object>
                    {
                        { "Id", id ?? 0 }
                    }
                };

                await MessageBus.PublishAsync($"{_dbDevice}", 
                    new MessageEnvelope($"{_dbDevice}", JsonSerializer.Serialize(updateRequest)), ct);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Error processing poll results: {Payload}", Config.Name, payloadStr);
        }

        return null;
    }
}
