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
        ILogger<PrintClientManager> logger, // Updated to match this specific manager class
        Func<IDeviceConfig, Serilog.ILogger, ITcpPrintClientBase> deviceFactory) 
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
        var logContext = Serilog.Log.ForContext("DeviceName", config.Name);

        // USE THE FACTORY: This solves the DI error and handles Brand logic via the Registrar
        var printer = DeviceFactory(config, logContext);

        return Task.FromResult(printer);
    }

    protected override void RegisterRouteSources(IDevice device)
    {
        /* Printers are passive listeners */
    }

    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv)
    {
        /* No inbound from printer */
    }

    protected override async Task HandleBusMessageAsync(IDevice device, string routeSource, string dest,
        MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (envelope.Payload is LabelToPrintMessage message && device is ITcpPrintClientBase dev)
            {
                // The ITcpPrinter interface provides the PrintAsync method [cite: 42]
                await dev.PrintAsync(message);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to route print job.", device.Config.Name);
        }
    }
}