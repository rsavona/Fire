
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common.Contracts
{
    public interface IDeviceManager
    {
        // Identification
        public Task<bool> TakeDeviceOfflineAsync(string deviceName);
        public Task ReinitializeDeviceAsync(string deviceName);

    }
}

public interface IDeviceManagerFactory
{
    IDeviceManager CreateDeviceManager(string managerType);
}