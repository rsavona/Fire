

namespace DeviceSpace.Common.Contracts;

public interface IDeviceManager 
{
    
}


public interface IDeviceManagerFactory
{
    IDeviceManager CreateDeviceManager(string managerType);
}