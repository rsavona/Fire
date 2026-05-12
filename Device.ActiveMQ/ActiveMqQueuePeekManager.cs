using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;

namespace Device.ActiveMQ;

public class ActiveMqQueuePeekManager : DeviceManagerBase<ActiveMqQueuePeekDevice>
{
    public ActiveMqQueuePeekManager(
        IMessageBus bus,
        List<IDeviceConfig> config,
        IFireLogger<ActiveMqQueuePeekManager> logger,
        Func<IDeviceConfig, IFireLogger, ActiveMqQueuePeekDevice> deviceFactory,
        string managerName)
        : base(bus, config, logger, deviceFactory, managerName)
    {
    }
}
