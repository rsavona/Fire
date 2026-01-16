using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Device.Connector.Plc; // Namespace where your PLC device lives

namespace Device.Connector.Plc;

public class PlcDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the device type itself
        services.AddTransient<PlcMultiClientDevice>();

        // 2. Register the Factory Delegate the Manager is asking for
        // This solves the "Unable to resolve service for type Func<...>" error
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, PlcMultiClientDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<PlcMultiClientDevice>(provider, config, logger);
            });
    }
}