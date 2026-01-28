using Device.Printer.Suite.Connector;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Printer.Suite;

public class PrintClientRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<PrintClientBaseDeviceZebra>();
        services.AddTransient<PrintClientBaseDeviceJetMark>();

        // Register the factory for ITcpPrinter
        services.AddTransient<Func<IDeviceConfig, Serilog.ILogger, ITcpPrintClientBase>>(provider => 
            (config, logger) => 
            {
                // The Brand logic lives here now, keeping the Manager clean
                var brand = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "Brand", "Zebra");

                return brand switch
                {
                    "JetMark" => ActivatorUtilities.CreateInstance<PrintClientBaseDeviceJetMark>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<PrintClientBaseDeviceZebra>(provider, config, logger)
                };
            });
    }
}