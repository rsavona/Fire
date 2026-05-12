using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using DeviceSpace.Common.Logging;

namespace Device.Nats;

public class NatsRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<NatsDevice>();
        services.AddSingleton<NatsManager>();
       
        services.AddTransient<Func<IDeviceConfig, IFireLogger, NatsDevice>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<NatsDevice>(provider, config, logger);
            });
    }
}
