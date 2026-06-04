using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Serilog;

namespace Fusion.Reaction.Tura.HostComm;

public class HostReaction : ReactionBase
{
    public HostReaction(IMessageBus bus, IReactionBlueprint config, ILogger logger)
        : base(bus, config, logger)
    {
    }

    /// <summary>
    /// Handles incoming Tura Host messages. Specifically, it echoes ROUT messages as ROUA.
    /// </summary>
    public async Task<object?> HandleTuraMessages(MessageEnvelope envelope, CancellationToken ct)
    {
        string payload = envelope.Payload?.ToString() ?? "{}";
        Logger.Verbose("[{Reaction}] HandleTuraMessages: Received payload: {Payload}", Config.Name, payload);

        try
        {
            var node = JsonNode.Parse(payload);
            if (node == null) return null;

            string msgType = node["MessageType"]?.GetValue<string>() ?? "";

            if (msgType == "ROUT")
            {
                string barcode = node["Barcode"]?.GetValue<string>() ?? "";
                string data = node["Data"]?.GetValue<string>() ?? "";

                Logger.Information("[{Reaction}] ROUT received for Barcode {Barcode}. Echoing ROUA.", Config.Name, barcode);
            
                // Construct the ROUA echo.
                return $"ROUA{barcode} {data}\r\n";
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Error processing Tura message for echo: {Payload}", Config.Name, payload);
            return null;
        }
    }

    /// <summary>
    /// Generates a SQL request to log host traffic using [usp_QueueMessage].
    /// </summary>
    public async Task<object?> HandleDbLogging(MessageEnvelope envelope, CancellationToken ct)
    {
        string payload = envelope.Payload?.ToString() ?? "{}";
        Logger.Verbose("[{Reaction}] HandleDbLogging: Received payload: {Payload}", Config.Name, payload);

        try
        {
            var node = JsonNode.Parse(payload);
            if (node == null) return null;

            string msgType = node["MessageType"]?.GetValue<string>() ?? "";

            // Only log ROUT messages and their acknowledgements
            if (msgType == "ROUT") 
            {
                string barcode = node["Barcode"]?.GetValue<string>() ?? "";
                string data = node["Data"]?.GetValue<string>() ?? "";

                // 1. Log the Inbound ROUT
                var inRequest = new
                {
                    Sql = "[usp_QueueMessage]",
                    Operation = "EXECUTE",
                    CommandType = nameof(System.Data.CommandType.StoredProcedure),
                    Parameters = new Dictionary<string, object?>
                    {
                        { "RawData", payload },
                        { "Direction", "IN" },
                        { "Type", "ROUT" },
                        { "Status", 0 },
                        { "StatusDescription", "RECEIVED" },
                        { "ElementName", envelope.Destination.ElementName }
                    }
                };

                // Manually publish the IN message to the DB topic
                await MessageBus.PublishAsync("TURA_DB.Log", 
                    new MessageEnvelope("TURA_DB.Log", System.Text.Json.JsonSerializer.Serialize(inRequest)), ct);

                // 2. Log the Outbound ROUA (Acknowledgement)
                var outRequest = new
                {
                    Sql = "[usp_QueueMessage]",
                    Operation = "EXECUTE",
                    CommandType = nameof(System.Data.CommandType.StoredProcedure),
                    Parameters = new Dictionary<string, object?>
                    {
                        { "RawData", $"ROUA{barcode} {data}"},
                        { "Direction", "OUT" },
                        { "Type", "ROUA" },
                        { "Status", 0 },
                        { "StatusDescription", "SENT" },
                        { "ElementName", envelope.Destination.ElementName }
                    }
                };

                Logger.Information("[{Reaction}] Logging transaction for Barcode {Barcode}: ROUT (IN) and ROUA (OUT)", Config.Name, barcode);
                
                // Return the OUT request to be published to the bond destination (TURA_DB.Log)
                return System.Text.Json.JsonSerializer.Serialize(outRequest);
            }

            Logger.Debug("[{Reaction}] Skipping DB logging for message type: {Type}", Config.Name, msgType);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Reaction}] Error generating log request: {Payload}", Config.Name, payload);
            return null;
        }
    }

    /// <summary>
    /// This method is triggered by a local scanner event (simulated or real).
    /// It calls [usp_GetSorterAssignment] which queues the CSCN message for the host.
    /// </summary>
    public async Task<object?> HandleScannerScan(MessageEnvelope envelope, CancellationToken ct)
    {
        string barcode = envelope.Payload?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(barcode)) return null;

        var sqlRequest = new
        {
            Sql = "[usp_GetSorterAssignment]",
            Operation = "EXECUTE",
            CommandType = nameof(System.Data.CommandType.StoredProcedure),
            Parameters = new Dictionary<string, object?>
            {
                { "LPN", barcode },
                { "SorterId", 1 },
                { "Gin", envelope.Gin }
            }
        };

        Logger.Information("[{Reaction}] Local Scan: {Barcode}. Requesting Assignment.", Config.Name, barcode);
        return System.Text.Json.JsonSerializer.Serialize(sqlRequest);
    }
}
