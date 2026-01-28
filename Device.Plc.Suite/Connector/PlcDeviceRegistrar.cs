using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

// Namespace where your PLC device lives

namespace Device.Plc.Suite.Connector;

public class PlcDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type itself
        services.AddTransient<PlcServerDevice>();

        // 2. Register the Factory Delegate the Manager is asking for
        // This solves the "Unable to resolve service for type Func<...>" error
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, PlcServerDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<PlcServerDevice>(provider, config, logger);
            });
    }
}