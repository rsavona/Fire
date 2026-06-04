using Fusion.Common.Blueprints;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Reaction.Sort.Standard;


/// <summary>
/// Registrar for the Print and Apply simulation reaction.
/// </summary>
public class PrintAndApplyFrcSimulationRegistrar : IElementRegistrar // Or IReactionRegistrar if your framework separates them
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the concrete reaction type as Transient
        // This allows the factory to create a new instance for each reaction configuration
        services.AddTransient<StandardSort>();
        // 2. Register the Factory Delegate
        // This is what the ReactionManager will invoke when it needs to spin up a new instance.
        // ActivatorUtilities handles the "IMessageBus" injection from the container automatically.
        services.AddTransient<Func<ReactionBlueprint, IFireLogger, StandardSort>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<StandardSort>(provider, config, logger);
            });
    }
}