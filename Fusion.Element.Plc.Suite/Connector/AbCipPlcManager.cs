using System.Text.Json;
using System.Text.Json.Nodes;
using Fusion.Element.Plc.Suite.Messages;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Plc.Suite.Connector;

public class AbCipPlcManager : ElementManagerBase<AbCipPlcElement>
{
    public AbCipPlcManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<AbCipPlcManager> logger,
        Func<IElementBlueprint, IFireLogger, AbCipPlcElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (!ElementInstances.TryGetValue(topic.ElementName, out var element)) return;

        try
        {
            ct.ThrowIfCancellationRequested();

            // Extract relevant data for logging
            var node = JsonNode.Parse(envelope.Payload?.ToString() ?? "{}");
            if (node == null || node is not JsonObject obj) return;

            string payload = envelope.Payload?.ToString() ?? "";
            await element.SendAsync(payload, ct);
            
            // Log the event using the structured logging helper
            Logger.LogConveyableEvent(element.Key.ElementName, $"CIP Response written to PLC: {payload}", envelope.Gin.ToString(), new List<string>(), "");
            Logger.Information("[{Dev}] CIP Response written to PLC for GIN {Gin}", element.Config.Name, envelope.Gin);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to write CIP response to PLC", element.Config.Name);
        }
    }
}
