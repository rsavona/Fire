using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Device.Support.CLI;

public class BlueprintVerifierManager : DeviceManagerBase<BlueprintVerifierDevice>
{
    public BlueprintVerifierManager(
        IMessageBus bus,
        List<IDeviceConfig> configs,
        IFireLogger<BlueprintVerifierManager> logger,
        Func<IDeviceConfig, IFireLogger, BlueprintVerifierDevice> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }
}
