using Fusion.Common.Blueprints;
using Microsoft.Extensions.Hosting;

namespace Fusion.Common.Contracts;

public interface IReactionFactory
{
    /// <summary>
    /// Creates a hosted service instance for the specified reaction configuration.
    /// </summary>
    /// <param name="config">The specific configuration for the reaction instance.</param>
    /// <returns>An IHostedService ready to run.</returns>
    IHostedService CreateReaction(ReactionBlueprint config);
}