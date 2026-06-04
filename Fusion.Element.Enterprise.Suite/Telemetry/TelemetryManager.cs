using System.Diagnostics;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Messaging;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Enterprise.Suite.Telemetry;

public class TelemetryManager : ElementManagerBase<TelemetryElement>
{
    public TelemetryManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<TelemetryManager> logger,
        Func<IElementBlueprint, IFireLogger, TelemetryElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task RegisterElementSourceBonds(IElement element)
    {
        // Telemetry is a global listener
        await MessageBus.SubscribeAsync(MessageBusTopic.DataFlow.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is FlowEvent flow && element is TelemetryElement tElement)
            {
                using var activity = tElement.StartActivity($"Bond: {flow.Force}");
                if (activity != null)
                {
                    activity.SetTag("fusion.source", flow.Source);
                    activity.SetTag("fusion.force", flow.Force);
                    activity.SetTag("fusion.destination", flow.Destination);
                    activity.SetTag("fusion.gin", envelope.Gin);
                }
            }
        });

        await MessageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is IElementStatus msg && element is TelemetryElement tElement)
            {
                // Export status as a metric or span event if needed
            }
        });
    }
}
