using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Device.Connector.Printer;
using DeviceSpace.Common.Configurations;

namespace Device.Connector.Printer;

public class PrinterRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the concrete implementations so ActivatorUtilities can find them
        services.AddTransient<BrandZebra>();
        services.AddTransient<BrandJetMark>();

        // 2. Register the specific Factory Delegate the Manager requires
        // We use PrinterDevice as the return type to avoid DI collisions with PLC or MQ factories.
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, ITcpPrinter>>(provider => 
            (config, logger) => 
            {
                // Move the Brand Logic here! 
                // This lets the factory decide WHICH concrete class to build.
                var brand = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "Brand", "Zebra");

                return brand switch
                {
                    "JetMark" => ActivatorUtilities.CreateInstance<BrandJetMark>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<BrandZebra>(provider, config, logger)
                };
            });
    }
}