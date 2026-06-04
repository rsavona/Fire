using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Support.CLI;

public class SimulationValidationRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<SimulationValidationElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, SimulationValidationElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<SimulationValidationElement>(provider, config, logger));
    }
}
