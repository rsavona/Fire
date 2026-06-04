
using Fusion.Common.Contracts;

namespace Fusion.Common.Contracts
{
    public interface IElementManager
    {
        // Identification
        public Task<bool> TakeDeviceOfflineAsync(string deviceName);
        public Task ReinitializeDeviceAsync(string deviceName);

    }
}

public interface IDeviceManagerFactory
{
    IElementManager CreateDeviceManager(string managerType);
}