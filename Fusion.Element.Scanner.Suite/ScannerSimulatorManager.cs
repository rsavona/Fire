using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Scanner.Suite;

/// <summary>
/// Manages one or more ScannerSimulatorElement instances.
/// </summary>
public class ScannerSimulatorManager : ElementManagerBase<ScannerSimulatorElement>
{
    public ScannerSimulatorManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<ScannerSimulatorElement>> logger,
        Func<IElementBlueprint, IFireLogger, ScannerSimulatorElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not ScannerSimulatorElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Information("[{Dev}] Scanner Simulator Data Forwarding to Bus: {Data}", element.Config.Name, env.Payload);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Simulator typically doesn't handle inbound bus messages, 
        // but we could implement a manual "trigger" if needed.
        return Task.CompletedTask;
    }
}
