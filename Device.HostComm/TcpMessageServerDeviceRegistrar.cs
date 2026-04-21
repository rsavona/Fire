using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.HostComm;

/// <summary>
/// Registers the TcpMessageServerDevice and its associated manager with the DI container.
/// </summary>
public class TcpMessageServerDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type
        services.AddTransient<TcpMessageServerDevice>();

        // 2. Register the Factory Delegate for the Manager
        services.AddTransient<Func<IDeviceConfig, IFireLogger, TcpMessageServerDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<TcpMessageServerDevice>(provider, config, logger);
            });

        // 3. The Manager itself will be discovered by type scanning in CoreServicesExtensions
        // if it inherits from IDeviceManager (via DeviceManagerBase) and is in the assembly.
    }
}
