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