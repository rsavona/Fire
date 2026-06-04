using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Fusion.Reaction.Simulation;

/// <summary>
/// Registrar for the Print and Apply simulation reaction.
/// </summary>
public class ReactionSimulationRegistrar : IElementRegistrar 
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the concrete reaction type as Transient
        // This allows the factory to create a new instance for each reaction configuration
        services.AddTransient<ConveyorTrackingPipeline>();
        services.AddTransient<ReactionSimulation>();
        
        // 2. Register the Factory Delegate
        // This is what the ReactionManager will invoke when it needs to spin up a new instance.
        // ActivatorUtilities handles the "IMessageBus" injection from the container automatically.
        services.AddTransient<Func<IReactionBlueprint, ILogger, ReactionSimulation>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<ReactionSimulation>(provider, config, logger);
            });
    }
}