using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;

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
