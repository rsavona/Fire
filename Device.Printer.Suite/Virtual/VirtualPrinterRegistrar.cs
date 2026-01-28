using Device.Virtual.Printer;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Printer.Suite.Virtual;

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