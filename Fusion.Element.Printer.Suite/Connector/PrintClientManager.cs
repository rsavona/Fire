using System.Text.Json.Nodes;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;


using Fusion.Common.Attributes;
using Fusion.Element.Printer.Suite.Virtual;

namespace Fusion.Element.Printer.Suite;

/// <summary>
/// A manager that creates and manages printer elements.
/// </summary>
[TestCounterpart(typeof(VirtualPrinterManager))]
public class PrintClientManager : ElementManagerBase<ITcpPrintClientBase>
{
    /// <summary>
    /// Constructor must be public for Dependency Injection to access it.
    /// </summary>
    public PrintClientManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<PrintClientManager> logger, // Updated to match this specific manager class
        Func<IElementBlueprint, IFireLogger, ITcpPrintClientBase> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
        ElementFactory = elementFactory;
    }

    /// <summary>
    /// Create a printer element using the injected factory.
    /// </summary>
    protected override Task<ITcpPrintClientBase> CreateElementAsync(IElementBlueprint config)
    {
        // Create a specific logger for this element instance
        var elementLogger = Logger.WithContext("ElementName", config.Name);

        var printer = ElementFactory(config, elementLogger);
        return Task.FromResult(printer);
    }

    protected override async Task RegisterElementSourceBonds(IElement element)

    {
        /* Printers are passive listeners */
        return;
    }

    protected override Task OnElementMessageToMessageBusAsync(object? sender, object messageEnv)
    {
        /* No inbound from printer */
        return Task.CompletedTask;
    }

    protected override async Task HandleBusMessageAsync(
        MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        ElementInstances.TryGetValue(topic.ElementName, out var element);
        try
        {
            var node = JsonNode.Parse(envelope.Payload.ToString());
            if (node == null) 
            {
                Logger.Error( "ERROR payload not in JSON");
                return;
            }
        
            var jsonObj = node.AsObject();
            var labelList = jsonObj["labels"]?.GetValue<List<string>>();  
            var lb = labelList.FirstOrDefault();
            var label = jsonObj["PrinterData"]?.GetValue<List<string>>();      
            // The ITcpPrinter interface provides the PrintAsync method [cite: 42]
            await element?.PrintAsync(label.ToString());
            
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to bond print job.", element.Config.Name);
        }
    }
}