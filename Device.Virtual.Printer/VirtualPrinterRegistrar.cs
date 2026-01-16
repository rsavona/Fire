using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Device.Virtual.Printer;

namespace Device.Virtual.Printer;

public class VirtualPrinterRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<VirtualPrinterSingleClientDevice>();

        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, VirtualPrinterSingleClientDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<VirtualPrinterSingleClientDevice>(provider, config, logger);
            });
    }
}