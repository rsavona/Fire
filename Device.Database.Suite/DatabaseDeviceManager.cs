using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Database.Suite;

/// <summary>
/// A manager that creates and manages database devices.
/// </summary>
public class DatabaseDeviceManager : DeviceManagerBase<IDatabaseDevice>
{
    public DatabaseDeviceManager(
        IMessageBus bus,
        List<IDeviceConfig> configs,
        IFireLogger<DatabaseDeviceManager> logger,
        Func<IDeviceConfig, IFireLogger, IDatabaseDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    protected override Task<IDatabaseDevice> CreateDeviceAsync(IDeviceConfig config)
    {
        var deviceLogger = Logger.WithContext("DeviceName", config.Name);
        var dbDevice = DeviceFactory(config, deviceLogger);
        return Task.FromResult(dbDevice);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var deviceName = envelope.Destination.DeviceName;
        if (DeviceInstances.TryGetValue(deviceName, out var device))
        {
            try
            {
                // Robustly parse the payload whether it is a string (JSON) or an object
                var payloadString = envelope.Payload is string s ? s : JsonSerializer.Serialize(envelope.Payload);
                var node = JsonNode.Parse(payloadString ?? "{}");

                var sql = node?["Sql"]?.GetValue<string>();
                var operation = node?["Operation"]?.GetValue<string>()?.ToUpper();
                var commandTypeStr = node?["CommandType"]?.GetValue<string>();
                
                var commandType = CommandType.Text;
                if (!string.IsNullOrEmpty(commandTypeStr) && Enum.TryParse<CommandType>(commandTypeStr, true, out var parsedType))
                {
                    commandType = parsedType;
                }

                if (string.IsNullOrEmpty(sql))
                {
                    Logger.LogWarning("[{Dev}] Received bus message with no SQL command.", deviceName);
                    return;
                }

                var parameters = ParseParameters(node?["Parameters"]);

                if (operation == "QUERY")
                {
                    var results = await device.QueryAsync<object>(sql, parameters, commandType);
                    
                    // Publish results back to the bus using the standard topic pattern: Device.QueryResult.Discriminator
                    var responseTopic = $"{deviceName}.QueryResult.{envelope.Destination.Discriminator}".TrimEnd('.');
                    var responsePayload = new
                    {
                        Results = results,
                        Sql = sql,
                        DecisionPoint = node?["DecisionPoint"]?.GetValue<string>() ?? node?["decisionPoint"]?.GetValue<string>(),
                        GIN = node?["GIN"]?.GetValue<int>() ?? node?["gin"]?.GetValue<int>() ?? envelope.Gin
                    };

                    await MessageBus.PublishAsync(responseTopic,
                        new MessageEnvelope(responseTopic, responsePayload, envelope.Gin), ct);
                    
                    Logger.Information("[{Dev}] Query results published to {Topic}", deviceName, responseTopic);
                }
                else
                {
                    await device.ExecuteAsync(sql, parameters, commandType);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Dev}] Manager failed to execute database operation.", deviceName);
            }
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
