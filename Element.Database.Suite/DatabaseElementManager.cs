using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Database.Suite;

/// <summary>
/// A manager that creates and manages database devices.
/// </summary>
public class DatabaseElementManager : ElementManagerBase<IDatabaseElement>
{
    public DatabaseElementManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<DatabaseElementManager> logger,
        Func<IElementBlueprint, IFireLogger, IDatabaseElement> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    protected override async Task<IDatabaseElement> CreateDeviceAsync(IElementBlueprint config)
    {
        var deviceLogger = Logger.WithContext("DeviceName", config.Name);
        var dbDevice = DeviceFactory(config, deviceLogger);

        if (dbDevice is DatabaseElementBase { Initialize: true } baseDevice)
        {
            try
            {
                Logger.Information("[{Dev}] Dynamic initialization triggered.", config.Name);
                await baseDevice.InitializeDatabaseAsync();
                Logger.Information("[{Dev}] Dynamic initialization completed. Updating configuration...", config.Name);
                
                // Update the configuration property to false and save
                await ConfigurationLoader.UpdateDevicePropertyAsync(config.Name, "Initialize", false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Dynamic initialization failed.", config.Name);
            }
        }

        return dbDevice;
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var deviceName = envelope.Destination.DeviceName;
        Logger.LogDebug("[{Dev}] Received bus message for topic: {Topic}", deviceName, envelope.Destination);

        if (DeviceInstances.TryGetValue(deviceName, out var device))
        {
            try
            {
                // Robustly parse the payload whether it is a string (JSON) or an object
                var payloadString = envelope.Payload is string s ? s : JsonSerializer.Serialize(envelope.Payload);
                Logger.LogDebug("[{Dev}] Parsing payload: {Payload}", deviceName, payloadString);

                var node = JsonNode.Parse(payloadString ?? "{}");
                if (node == null || node is not JsonObject topObj) 
                {
                    Logger.LogWarning("[{Dev}] Failed to parse payload as JsonObject. Payload: {Payload}", deviceName, payloadString);
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
                    Logger.LogWarning("[{Dev}] Received bus message with no SQL command. Payload: {Payload}", deviceName, payloadString);
                    return;
                }

                var parameters = ParseParameters(GetProp(topObj, "Parameters"));

                if (operation == "QUERY")
                {
                    Logger.LogInfo("[{Dev}] Executing SQL Query: {Sql} with Params: {Params}", deviceName, sql, JsonSerializer.Serialize(parameters));
                    var results = await device.QueryAsync<dynamic>(sql, parameters, commandType);
                    
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

                    // Publish results back to the bus using the standard topic pattern: Device.QueryResult.Discriminator
                    var responseTopic = $"{deviceName}.QueryResult.{envelope.Destination.Discriminator}".TrimEnd('.');
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
                    
                    Logger.LogInfo("[{Dev}] Query results published to {Topic}: {Count} rows", deviceName, responseTopic, serializableResults.Count);
                }
                else
                {
                    Logger.LogInfo("[{Dev}] Executing SQL Command: {Sql} with Params: {Params}", deviceName, sql, JsonSerializer.Serialize(parameters));
                    await device.ExecuteAsync(sql, parameters, commandType);
                    Logger.LogInfo("[{Dev}] SQL Command Executed Successfully.", deviceName);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Dev}] Manager failed to execute database operation.", deviceName);
            }
        }
        else
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", deviceName);
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
