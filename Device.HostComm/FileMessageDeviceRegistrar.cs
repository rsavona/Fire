using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.HostComm;

public class FileMessageDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<FileMessageDevice>();

        services.AddTransient<Func<IDeviceConfig, IFireLogger, FileMessageDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<FileMessageDevice>(provider, config, logger);
            });
    }
}
