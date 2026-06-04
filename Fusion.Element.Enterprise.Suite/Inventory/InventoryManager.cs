using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Messaging;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Enterprise.Suite.Inventory;

public class InventoryManager : ElementManagerBase<InventoryElement>
{
    public InventoryManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<InventoryManager> logger,
        Func<IElementBlueprint, IFireLogger, InventoryElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task RegisterElementSourceBonds(IElement element)
    {
        // Inventory is a passive observer of all system traffic
        await MessageBus.SubscribeAsync(MessageBusTopic.DataFlow.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is FlowEvent flow && element is InventoryElement invElement)
            {
                // Source is where it was, Destination is where it is going
                invElement.UpdateContainer(envelope.Gin, flow.Destination, flow.Force);
            }
        });

        // Also allow direct queries for container state
        await MessageBus.SubscribeAsync($"{element.Config.Name}.QUERY", async (envelope, ct) =>
        {
            if (envelope.Payload is int gin && element is InventoryElement invElement)
            {
                var state = invElement.GetContainer(gin);
                var responseTopic = $"{element.Config.Name}.RESULT.{gin}";
                await MessageBus.PublishAsync(responseTopic, new MessageEnvelope(responseTopic, state ?? (object)"NOT_FOUND"));
            }
        });
    }
}
