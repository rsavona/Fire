using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Plc.Suite.Virtual;

public class VirtualPlcRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Register the Factory Delegate
        // This is where VirtualPlcManager will actually create the element instances.
        services.AddTransient<Func<IElementBlueprint, IFireLogger, VirtualPlcElement>>(provider => 
            (config, logger) => 
            { 
              
                return ActivatorUtilities.CreateInstance<VirtualPlcElement>(provider, config, logger);
            });
    }
}