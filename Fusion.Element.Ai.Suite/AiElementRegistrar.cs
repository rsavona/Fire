using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Ai.Suite;

/// <summary>
/// Registers the AiElement and its associated manager with the DI container.
/// </summary>
public class AiElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the element type
        services.AddTransient<AiElement>();

        // 2. Register the Factory Delegate for the Manager
        services.AddTransient<Func<IElementBlueprint, IFireLogger, AiElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<AiElement>(provider, config, logger);
            });
    }
}
