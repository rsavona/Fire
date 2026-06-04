using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Serilog;

namespace Fusion.Reaction.Tura.HostComm;

public class HostOutputReaction : ReactionBase
{
    private readonly string _dbElement;
    private readonly string _targetHostElement;
    private readonly int _pollingIntervalMs;

    public HostOutputReaction(IMessageBus bus, ReactionBlueprint config, ILogger logger)
        : base(bus, config, logger)
    {
        _dbElement = config.Properties.TryGetValue("DbElement", out var db) ? db.ToString() ?? "" : "";
        _targetHostElement = config.Properties.TryGetValue("TargetHostElement", out var host) ? host.ToString() ?? "" : "";
        _pollingIntervalMs = config.Properties.TryGetValue("PollingIntervalMs", out var interval) ? Convert.ToInt32(interval) : 1000;

        Logger.Information("[{Reaction}] Tura Host Output Reaction Initialized. DB: {DB}, Host: {Host}, Polling: {Interval}ms", 
            Config.Name, _dbElement, _targetHostElement, _pollingIntervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // Ensure bonds are initialized before starting the loop
        await InitializeBondsAsync();
        UpdateStatus(ReactionState.Active, ReactionEvent.Started, ElementHealth.Normal, "Reaction Active.");

        Logger.Information("[{Reaction}] Starting MessageQueue polling loop...", Config.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollingIntervalMs, stoppingToken);

                if (IsSystemStarted && !string.IsNullOrEmpty(_dbElement))
                {
                    // Request messages from the MessageQueue table
                    // Use a discriminator in the topic so the response comes back on TURA_DB.QueryResult.PollOutputQueue
                    var pollRequest = new
                    {
                        Sql = "SELECT TOP 10 Id, RawData FROM MessageQueue WHERE ElementName = @ElementName AND Status = 0 ORDER BY Id",
                        Operation = "QUERY",
                        Parameters = new Dictionary<string, object>
                        {
                            { "ElementName", _targetHostElement }
                        },
                        DecisionPoint = "PollOutputQueue"
                    };

                    var topic = $"{_dbElement}.Query.PollOutputQueue";
                    await MessageBus.PublishAsync(topic, 
                        new MessageEnvelope(topic, JsonSerializer.Serialize(pollRequest)), stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Reaction}] Error in polling loop", Config.Name);
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

                Logger.Information("[{Reaction}] Sending message from queue (ID: {Id}) to {Target}: {Payload}", 
                    Config.Name, id, _targetHostElement, rawPayload);

                // Send to Host TCP Server (with CR). 
                // The 'rawPayload' here is the pipe-delimited string (e.g., "BC|CSCN|...") 
                // exactly as the host expects.
                var hostEnvelope = new MessageEnvelope($"{_targetHostElement}.Outbound.Queue", rawPayload + "\r\n");
                await MessageBus.PublishAsync($"{_targetHostElement}.Outbound.Queue", hostEnvelope, ct);

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

                await MessageBus.PublishAsync($"{_dbElement}", 
                    new MessageEnvelope($"{_dbElement}", JsonSerializer.Serialize(updateRequest)), ct);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Error processing poll results: {Payload}", Config.Name, payloadStr);
        }

        return null;
    }
}
