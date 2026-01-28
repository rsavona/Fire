using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.ActiveMQ;

public class ActiveMqRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type
        services.AddTransient<ActiveMqDevice>();
       
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, ActiveMqDevice>>(provider =>
            (config, logger) =>
            {
                // Now it only needs config and logger
                return ActivatorUtilities.CreateInstance<ActiveMqDevice>(provider, config, logger);
            });
    }
}