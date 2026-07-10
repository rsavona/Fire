using System.Data;
using System.Text.Json;
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

        if (!ElementInstances.TryGetValue(elementName, out var element))
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", elementName);
            return;
        }

        if (!TryParseCommand<DatabaseCommand>(envelope, out var command) || command == null) return;

        if (string.IsNullOrEmpty(command.Sql))
        {
            Logger.LogWarning("[{Dev}] Received database command with no SQL. Topic: {Topic}", elementName, envelope.Destination);
            return;
        }

        try
        {
            var commandType = CommandType.Text;
            if (!string.IsNullOrEmpty(command.CommandType) &&
                Enum.TryParse<CommandType>(command.CommandType, true, out var parsedType))
            {
                commandType = parsedType;
            }

            var parameters = ConvertParameters(command.Parameters);

            // The verb comes from the topic when specified (DB1.Query / DB1.Execute),
            // falling back to the payload's Operation field for legacy publishers.
            string verb = envelope.Destination.MessageType.ToUpperInvariant() is "QUERY" or "EXECUTE"
                ? envelope.Destination.MessageType.ToUpperInvariant()
                : command.Operation?.ToUpperInvariant() ?? "EXECUTE";

            if (verb == "QUERY")
            {
                Logger.LogInfo("[{Dev}] Executing SQL Query: {Sql} with Params: {Params}", elementName, command.Sql, JsonSerializer.Serialize(parameters));
                var results = await element.QueryAsync<dynamic>(command.Sql, parameters, commandType);

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
                    Sql = command.Sql,
                    command.DecisionPoint,
                    GIN = command.Gin ?? envelope.Gin
                };

                // Serialize to JSON string so subscribers can parse it using .ToString() and JsonNode.Parse()
                var jsonResponse = JsonSerializer.Serialize(responsePayload);

                await MessageBus.PublishAsync(responseTopic,
                    new MessageEnvelope(responseTopic, jsonResponse, envelope.Gin,
                        header: envelope.DeriveHeader(elementName)), ct);

                Logger.LogInfo("[{Dev}] Query results published to {Topic}: {Count} rows", elementName, responseTopic, serializableResults.Count);
            }
            else
            {
                Logger.LogInfo("[{Dev}] Executing SQL Command: {Sql} with Params: {Params}", elementName, command.Sql, JsonSerializer.Serialize(parameters));
                await element.ExecuteAsync(command.Sql, parameters, commandType);
                Logger.LogInfo("[{Dev}] SQL Command Executed Successfully.", elementName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to execute database operation.", elementName);
        }
    }

    internal static Dictionary<string, object?>? ConvertParameters(Dictionary<string, JsonElement>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in parameters)
        {
            dict[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                // Cast to object so the conditional doesn't unify long/double to double,
                // which would silently send integer parameters as floating point.
                JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Object => value.GetRawText(),
                JsonValueKind.Array => value.GetRawText(),
                _ => value.ToString()
            };
        }
        return dict;
    }
}
