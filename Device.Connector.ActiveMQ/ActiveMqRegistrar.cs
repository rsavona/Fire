using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Connector.ActiveMQ;


public class ActiveMqRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type
        services.AddTransient<ActiveMqDevice>();

        // 2. Register the Factory Delegate required by ActiveMqManager
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, ActiveMqDevice>>(provider => 
            (config, logger) => 
            {
                // Uses ActivatorUtilities to inject IMessageBus and other system services 
                // into the ActiveMqDevice constructor along with the config/logger.
                return ActivatorUtilities.CreateInstance<ActiveMqDevice>(provider, config, logger);
            });
    }
}