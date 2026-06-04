using Fusion.Element.Virtual.Printer;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Printer.Suite.Virtual;

public class VirtualPrinterManager : ElementManagerBase<VirtualPrintElement>
{
    public VirtualPrinterManager(IMessageBus bus, List<IElementBlueprint> configs, IFireLogger<ElementManagerBase<VirtualPrintElement>> logger, 
            Func<IElementBlueprint, IFireLogger, VirtualPrintElement> elementFactory,
            string managerName)
        : base(bus, configs, logger, elementFactory, managerName) { }
    
    protected override Task OnElementMessageToMessageBusAsync(object? sender, object messageEnv) { return Task.CompletedTask;}


    protected override Task HandleBusMessageAsync( MessageEnvelope envelope, CancellationToken ct)
    {
          var topic = envelope.Destination; 
        ElementInstances.TryGetValue(topic.ElementName, out var element);
        if (element is null)
        {
            Logger.Error(" Not proper element in HandleBusMessageAsync VirtualPrinterManager");
             return Task.CompletedTask;
        }
        Logger.Information("[{Dev}] VIRTUAL-PRINT >> Simulating job {msg}", element.Config.Name, envelope.Payload);
        return Task.CompletedTask;
    }
}