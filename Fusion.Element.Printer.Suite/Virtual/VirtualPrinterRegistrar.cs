using Fusion.Element.Virtual.Printer;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Printer.Suite.Virtual;

public class VirtualPrinterRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<VirtualPrintElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, VirtualPrintElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<VirtualPrintElement>(provider, config, logger);
            });
    }
}