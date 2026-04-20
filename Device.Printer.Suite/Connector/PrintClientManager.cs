using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;


namespace Device.Printer.Suite;

/// <summary>
/// A manager that creates and manages printer devices.
/// </summary>
public class PrintClientManager : DeviceManagerBase<ITcpPrintClientBase>
{
    /// <summary>
    /// Constructor must be public for Dependency Injection to access it.
    /// </summary>
    public PrintClientManager(
        IMessageBus bus,
        List<IDeviceConfig> configs,
        IFireLogger<PrintClientManager> logger, // Updated to match this specific manager class
        Func<IDeviceConfig, IFireLogger, ITcpPrintClientBase> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
        DeviceFactory = deviceFactory;
    }

    /// <summary>
    /// Create a printer device using the injected factory.
    /// </summary>
    protected override Task<ITcpPrintClientBase> CreateDeviceAsync(IDeviceConfig config)
    {
        // Create a specific logger for this device instance
        var deviceLogger = Logger.WithContext("DeviceName", config.Name);

        var printer = DeviceFactory(config, deviceLogger);
        return Task.FromResult(printer);
    }

    protected override Task RegisterDeviceSourceRoutes(IDevice device)
    {
        /* Printers are passive listeners */
        return Task.CompletedTask;
    }

    protected override Task OnDeviceMessageToMessageBusAsync(object? sender, object messageEnv)
    {
        /* No inbound from printer */
        return Task.CompletedTask;
    }

    protected override async Task HandleBusMessageAsync(
        MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        DeviceInstances.TryGetValue(topic.DeviceName, out var device);
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
            await device?.PrintAsync(label.ToString());
            
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to route print job.", device.Config.Name);
        }
    }
}