using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.HostComm;

/// <summary>
/// Registers the TcpMessageClientDevice and its associated manager with the DI container.
/// </summary>
public class TcpMessageClientDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type
        services.AddTransient<TcpMessageClientDevice>();

        // 2. Register the Factory Delegate for the Manager
        services.AddTransient<Func<IDeviceConfig, IFireLogger, TcpMessageClientDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<TcpMessageClientDevice>(provider, config, logger);
            });
    }
}
