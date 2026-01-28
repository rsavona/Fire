using Device.Virtual.Printer;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Printer.Suite.Virtual;

public class VirtualPrinterManager : DeviceManagerBase<VirtualPrintDevice>
{
    public VirtualPrinterManager(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<VirtualPrintDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, VirtualPrintDevice> deviceFactory)
        : base(bus, configs, logger,deviceFactory) { }



    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv) { }


    protected override Task HandleBusMessageAsync(IDevice clientDevice, string routeSource,  string dest, MessageEnvelope envelope, CancellationToken ct)
    {
        Logger.LogInformation("[{Dev}] VIRTUAL-PRINT >> Simulating job from {Source}", clientDevice.Config.Name, routeSource);
        return Task.CompletedTask;
    }
}