using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Device.Virtual.Plc;

namespace Device.Virtual.Plc;

public class VirtualPlcRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<VirtualPlcDevice>();

        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, VirtualPlcDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<VirtualPlcDevice>(provider, config, logger);
            });
    }
}