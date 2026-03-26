using Device.Virtual.Printer;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Printer.Suite.Virtual;

public class VirtualPrinterManager : DeviceManagerBase<VirtualPrintDevice>
{
    public VirtualPrinterManager(IMessageBus bus, List<IDeviceConfig> configs, IFireLogger<DeviceManagerBase<VirtualPrintDevice>> logger, 
            Func<IDeviceConfig, IFireLogger, VirtualPrintDevice> deviceFactory)
        : base(bus, configs, logger,deviceFactory) { }
    
    protected override Task OnDeviceMessageToMessageBusAsync(object? sender, object messageEnv) { return Task.CompletedTask;}


    protected override Task HandleBusMessageAsync( MessageEnvelope envelope, CancellationToken ct)
    {
          var topic = envelope.Destination; 
        DeviceInstances.TryGetValue(topic.DeviceName, out var device);
        if (device is null)
        {
            Logger.Error(" Not proper device in HandleBusMessageAsync VirtualPrinterManager");
             return Task.CompletedTask;
        }
        Logger.Information("[{Dev}] VIRTUAL-PRINT >> Simulating job {msg}", device.Config.Name, envelope.Payload);
        return Task.CompletedTask;
    }
}