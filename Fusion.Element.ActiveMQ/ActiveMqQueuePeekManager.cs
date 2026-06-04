using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;

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
