using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceTest.Common;

public class TestableManager<TDevice> : DeviceManagerBase<TDevice> 
    where TDevice : class, IDevice
{
    public TestableManager(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<TDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, TDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory) { }

    public IEnumerable<TDevice> GetDevices() => DeviceInstances;

    // Standard overrides required by the abstract base
    protected override void RegisterRouteSources(IDevice device) { }
    protected override void OnDeviceMessageReceivedAsync(object? sender, object messageEnv) { }
    protected override Task HandleBusMessageAsync(IDevice device, string routeSource,  string dest, MessageEnvelope envelope, CancellationToken ct) 
        => Task.CompletedTask;
}