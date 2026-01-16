using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Connector.Printer;
/// <summary>
/// A manager that creates and manages printer devices.
/// </summary>
public class PrinterManager : DeviceManagerBase<IDevice>
{
    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    public PrinterManager(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<IDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, IDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Create a printer device.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    protected override Task<IDevice> CreateDeviceAsync(IDeviceConfig config)
    {
        var brand = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "Brand", "Zebra");
        var logContext = Serilog.Log.ForContext("DeviceName", config.Name);

        IDevice printer = brand switch
        {
            "JetMark" => new BrandJetMark(config, logContext),
            _ => new BrandZebra(config, logContext)
        };

        return Task.FromResult(printer);
    }

    /// <summary>
    /// Printers are passive listeners.
    /// </summary>
    /// <param name="device"></param>
    protected override void RegisterDeviceHandlers(IDevice device)
    {
        /* Printers are passive listeners */
    }

    /// <summary>
    /// Printers do not send messages. The status is handled e
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="messageEnv"></param>
    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv)
    {
        /* No inbound from printer */
    }

    protected override async Task HandleBusMessageAsync(IDevice device, string routeSource, string dest,
        MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (device is ITcpPrinter printer && envelope.Payload is LabelToPrintMessage message)
            {
                // The Manager keeps the device instance alive, so the TCP connection stays open
                await printer.PrintAsync(message);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Manager failed to route print job.", device.Config.Name);
        }
    }
}