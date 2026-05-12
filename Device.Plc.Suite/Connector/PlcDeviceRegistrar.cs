using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

// Namespace where your PLC device lives

namespace Device.Plc.Suite.Connector;

public class PlcDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Standard PLC Server
        services.AddTransient<PlcServerDevice>();
        services.AddTransient<Func<IDeviceConfig, IFireLogger, PlcServerDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<PlcServerDevice>(provider, config, logger);
            });

        // 2. Allen-Bradley CIP PLC
        services.AddTransient<AbCipPlcDevice>();
        services.AddTransient<Func<IDeviceConfig, IFireLogger, AbCipPlcDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<AbCipPlcDevice>(provider, config, logger);
            });
    }
}
