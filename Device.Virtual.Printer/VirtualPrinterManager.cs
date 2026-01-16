using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Virtual.Printer;

public class VirtualPrinterManager : DeviceManagerBase<VirtualPrinterSingleClientDevice>
{
    public VirtualPrinterManager(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<VirtualPrinterSingleClientDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, VirtualPrinterSingleClientDevice> deviceFactory)
        : base(bus, configs, logger,deviceFactory) { }

    protected override void RegisterDeviceHandlers(VirtualPrinterSingleClientDevice clientDevice) { }

    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv) { }

    protected override Task HandleBusMessageAsync(VirtualPrinterSingleClientDevice clientDevice, string routeSource,  string dest, MessageEnvelope envelope, CancellationToken ct)
    {
        Logger.LogInformation("[{Dev}] VIRTUAL-PRINT >> Simulating job from {Source}", clientDevice.Config.Name, routeSource);
        return Task.CompletedTask;
    }
}