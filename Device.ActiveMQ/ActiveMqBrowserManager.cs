using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;

namespace Device.ActiveMQ;

public class ActiveMqBrowserManager : DeviceManagerBase<ActiveMqBrowserDevice>
{
    public ActiveMqBrowserManager(
        IMessageBus bus,
        List<IDeviceConfig> config,
        IFireLogger<ActiveMqBrowserManager> logger,
        Func<IDeviceConfig, IFireLogger, ActiveMqBrowserDevice> deviceFactory,
        string managerName)
        : base(bus, config, logger, deviceFactory, managerName)
    {
    }
}
