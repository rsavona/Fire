using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Plc.Suite.Virtual;

public class VirtualPlcRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Register the Factory Delegate
        // This is where VirtualPlcManager will actually create the device instances.
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, VirtualPlcDevice>>(provider => 
            (config, logger) => 
            { 
              
                return ActivatorUtilities.CreateInstance<VirtualPlcDevice>(provider, config, logger);
            });
    }
}