using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Scanner.Suite;

public class ScannerServerManager : ElementManagerBase<ScannerServerElement>
{
    public ScannerServerManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<ScannerServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, ScannerServerElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not ScannerServerElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Information("[{Dev}] Scanner Data Forwarding to Bus: {Data}", element.Config.Name, env.Payload);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Typically, we don't send data BACK to a scanner, but we might send a trigger command.
        if (!ElementInstances.TryGetValue(envelope.Destination.ElementName, out var element))
        {
            Logger.Warning("[{Dev}] Received bus message but element instance not found.", envelope.Destination.ElementName);
            return;
        }

        await element.SendAsync(envelope.GetPayloadText(), ct);
    }
}
