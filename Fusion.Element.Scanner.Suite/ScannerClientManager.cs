using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Scanner.Suite;

public class ScannerClientManager : ElementManagerBase<ScannerClientElement>
{
    public ScannerClientManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<ScannerClientElement>> logger,
        Func<IElementBlueprint, IFireLogger, ScannerClientElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not ScannerClientElement element || messEnv is not MessageEnvelope env) return;

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (!ElementInstances.TryGetValue(envelope.Destination.ElementName, out var element))
        {
            Logger.Warning("[{Dev}] Received bus message but element instance not found.", envelope.Destination.ElementName);
            return;
        }

        // If we receive a message from the bus, we treat it as a "Trigger Scan" command
        string barcode = envelope.GetPayloadText();
        if (string.IsNullOrEmpty(barcode)) barcode = "SCAN-TRIGGERED";
        await element.SendScanAsync(barcode, ct);
    }
}
