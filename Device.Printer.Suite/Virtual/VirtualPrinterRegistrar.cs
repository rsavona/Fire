using Device.Virtual.Printer;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Printer.Suite.Virtual;

public class VirtualPrinterRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<VirtualPrintDevice>();

        services.AddTransient<Func<IDeviceConfig, IFireLogger, VirtualPrintDevice>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<VirtualPrintDevice>(provider, config, logger);
            });
    }
}