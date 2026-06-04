using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Database.Suite;

/// <summary>
/// A manager that creates and manages database elements.
/// </summary>
public class DatabaseElementManager : ElementManagerBase<IDatabaseElement>
{
    public DatabaseElementManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<DatabaseElementManager> logger,
        Func<IElementBlueprint, IFireLogger, IDatabaseElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task<IDatabaseElement> CreateElementAsync(IElementBlueprint config)
    {
        var elementLogger = Logger.WithContext("ElementName", config.Name);
        var dbElement = ElementFactory(config, elementLogger);

        if (dbElement is DatabaseElementBase { Initialize: true } baseElement)
        {
            try
            {
                Logger.Information("[{Dev}] Dynamic initialization triggered.", config.Name);
                await baseElement.InitializeDatabaseAsync();
                Logger.Information("[{Dev}] Dynamic initialization completed. Updating configuration...", config.Name);
                
                // Update the configuration property to false and save
                await ConfigurationLoader.UpdateElementPropertyAsync(config.Name, "Initialize", false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Dynamic initialization failed.", config.Name);
            }
        }

        return dbElement;
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var elementName = envelope.Destination.ElementName;
        Logger.LogDebug("[{Dev}] Received bus message for topic: {Topic}", elementName, envelope.Destination);

        if (ElementInstances.TryGetValue(elementName, out var element))
        {
            try
            {
                // Robustly parse the payload whether it is a string (JSON) or an object
                var payloadString = envelope.Payload is string s ? s : JsonSerializer.Serialize(envelope.Payload);
                Logger.LogDebug("[{Dev}] Parsing payload: {Payload}", elementName, payloadString);

                var node = JsonNode.Parse(payloadString ?? "{}");
                if (node == null || node is not JsonObject topObj) 
                {
                    Logger.LogWarning("[{Dev}] Failed to parse payload as JsonObject. Payload: {Payload}", elementName, payloadString);
                    return;
                }

                // Helper to get property case-insensitively
                JsonNode? GetProp(JsonObject o, string key) => 
                    o.FirstOrDefault(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

                var sql = GetProp(topObj, "Sql")?.GetValue<string>();
                var operation = GetProp(topObj, "Operation")?.GetValue<string>()?.ToUpper();
                var commandTypeStr = GetProp(topObj, "CommandType")?.GetValue<string>();
                
                var commandType = CommandType.Text;
                if (!string.IsNullOrEmpty(commandTypeStr) && Enum.TryParse<CommandType>(commandTypeStr, true, out var parsedType))
                {
                    commandType = parsedType;
                }

                if (string.IsNullOrEmpty(sql))
                {
                    Logger.LogWarning("[{Dev}] Received bus message with no SQL command. Payload: {Payload}", elementName, payloadString);
                    return;
                }

                var parameters = ParseParameters(GetProp(topObj, "Parameters"));

                if (operation == "QUERY")
                {
                    Logger.LogInfo("[{Dev}] Executing SQL Query: {Sql} with Params: {Params}", elementName, sql, JsonSerializer.Serialize(parameters));
                    var results = await element.QueryAsync<dynamic>(sql, parameters, commandType);
                    
                    var serializableResults = new List<Dictionary<string, object?>>();
                    foreach (var row in results)
                    {
                        if (row is IDictionary<string, object> dict)
                        {
                            serializableResults.Add(new Dictionary<string, object?>(dict));
                        }
                        else
                        {
                            serializableResults.Add(new Dictionary<string, object?> { ["Value"] = row?.ToString() });
                        }
                    }

                    // Publish results back to the bus using the standard topic pattern: Fusion.Element.QueryResult.Discriminator
                    var responseTopic = $"{elementName}.QueryResult.{envelope.Destination.Discriminator}".TrimEnd('.');
                    var responsePayload = new
                    {
                        Results = serializableResults,
                        Sql = sql,
                        DecisionPoint = GetProp(topObj, "DecisionPoint")?.GetValue<string>(),
                        GIN = GetProp(topObj, "GIN")?.GetValue<int>() ?? envelope.Gin
                    };

                    // Serialize to JSON string so subscribers can parse it using .ToString() and JsonNode.Parse()
                    var jsonResponse = JsonSerializer.Serialize(responsePayload);

                    await MessageBus.PublishAsync(responseTopic,
                        new MessageEnvelope(responseTopic, jsonResponse, envelope.Gin), ct);
                    
                    Logger.LogInfo("[{Dev}] Query results published to {Topic}: {Count} rows", elementName, responseTopic, serializableResults.Count);
                }
                else
                {
                    Logger.LogInfo("[{Dev}] Executing SQL Command: {Sql} with Params: {Params}", elementName, sql, JsonSerializer.Serialize(parameters));
                    await element.ExecuteAsync(sql, parameters, commandType);
                    Logger.LogInfo("[{Dev}] SQL Command Executed Successfully.", elementName);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Dev}] Manager failed to execute database operation.", elementName);
            }
        }
        else
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", elementName);
        }
    }

    private static Dictionary<string, object?>? ParseParameters(JsonNode? parametersNode)
    {
        if (parametersNode == null || parametersNode is not JsonObject obj) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var kvp in obj)
        {
            if (kvp.Value == null)
            {
                dict[kvp.Key] = null;
                continue;
            }

            dict[kvp.Key] = kvp.Value.GetValueKind() switch
            {
                JsonValueKind.String => kvp.Value.GetValue<string>(),
                JsonValueKind.Number => kvp.Value.AsValue().TryGetValue<long>(out var l) ? l : kvp.Value.AsValue().GetValue<double>(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => kvp.Value.ToJsonString(),
                JsonValueKind.Array => kvp.Value.ToJsonString(),
                _ => kvp.Value.ToString()
            };
        }
        return dict;
    }
}
