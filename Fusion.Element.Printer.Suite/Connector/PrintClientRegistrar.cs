using Fusion.Element.Printer.Suite.Connector;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Printer.Suite;

public class PrintClientRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<PrintClientElementZebra>();
        services.AddTransient<PrintClientBaseElementJetMark>();

        // Register the factory for ITcpPrinter
        services.AddTransient<Func<IElementBlueprint, IFireLogger, ITcpPrintClientBase>>(provider => 
            (config, logger) => 
            {
                // The Brand logic lives here now, keeping the Manager clean
                var brand = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "Brand", "Zebra");

                return brand switch
                {
                    "JetMark" => ActivatorUtilities.CreateInstance<PrintClientBaseElementJetMark>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<PrintClientElementZebra>(provider, config, logger)
                };
            });
    }
}