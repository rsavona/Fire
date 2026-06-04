using System;
using System.Collections.Generic;
using System.Linq;
using Fusion.Common;
using Fusion.Common.Blueprints;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fusion.Core;

public class ReactionFactory : IReactionFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IEnumerable<Type> _loadedReactionTypes;

    // We inject the list of Types that we discovered during the 'AddCoreServices' phase.
    // We use [FromKeyedServices] or just a specific registered IEnumerable if possible, 
    // but simpler is to register a specific wrapper or just IEnumerable<Type> if unique.
    public ReactionFactory(IServiceProvider serviceProvider, [FromKeyedServices("ReactionTypes")] IEnumerable<Type> loadedReactionTypes)
    {
        _serviceProvider = serviceProvider;
        _loadedReactionTypes = loadedReactionTypes;
    }

    public IHostedService CreateReaction(ReactionBlueprint config)
    {
        if (string.IsNullOrEmpty(config.Type))
        {
            throw new ArgumentException($"Reaction configuration '{config.Name}' is missing the 'Type' property.");
        }

        // Find the matching type in our loaded list
        // We match case-insensitive on the Class CustomerName (e.g., "ReactionPrintAndApplyFrc")
        var reactionType = _loadedReactionTypes
            .FirstOrDefault(t => t.Name.Equals(config.Type, StringComparison.OrdinalIgnoreCase) 
                                 && typeof(BackgroundService).IsAssignableFrom(t)
                                 && !t.IsAbstract);

        if (reactionType == null)
        {
            Serilog.Log.Error("Could not find a force implementation for type '{ReactionType}'. Ensure the DLL is loaded and the class inherits from ReactionBase.", config.Type);
            throw new InvalidOperationException(
                $"Could not find a force implementation for type '{config.Type}'. " +
                $"Ensure the DLL is loaded and the class inherits from ReactionBase.");
        }

    
        try
        {
            // ActivatorUtilities.CreateInstance is powerful:
            // - It pulls dependencies (IMessageBus) from _serviceProvider.
            // - It pushes explicit arguments (config) into the constructor where types match.
            object instance = ActivatorUtilities.CreateInstance(_serviceProvider, reactionType, config);

            if (instance is IHostedService service)
            {
                return service;
            }

            throw new InvalidCastException($"Type '{reactionType.Name}' does not implement IHostedService.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create force '{config.Name}' of type '{config.Type}': {ex.Message}", ex);
        }
    }
}