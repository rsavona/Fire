using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.ActiveMQ;

public class ActiveMqRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type
        services.AddTransient<ActiveMqDevice>();
        services.AddTransient<ActiveMqBrowserDevice>();
        services.AddTransient<ActiveMqQueuePeekDevice>();
       
        services.AddTransient<Func<IDeviceConfig, IFireLogger, ActiveMqDevice>>(provider =>
            (config, logger) =>
            {
                // Now it only needs config and logger
                return ActivatorUtilities.CreateInstance<ActiveMqDevice>(provider, config, logger);
            });

        services.AddTransient<Func<IDeviceConfig, IFireLogger, ActiveMqBrowserDevice>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<ActiveMqBrowserDevice>(provider, config, logger);
            });

        services.AddTransient<Func<IDeviceConfig, IFireLogger, ActiveMqQueuePeekDevice>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<ActiveMqQueuePeekDevice>(provider, config, logger);
            });
    }
}