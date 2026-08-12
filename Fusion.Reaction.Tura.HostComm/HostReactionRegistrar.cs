using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Reaction.Tura.HostComm;

public class HostReactionRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Add the reaction types to the collection of available reactions
        services.AddKeyedSingleton("ReactionTypes", typeof(HostReaction));
        services.AddKeyedSingleton("ReactionTypes", typeof(ReactionTester));
        services.AddKeyedSingleton("ReactionTypes", typeof(HostOutputReaction));
        services.AddKeyedSingleton("ReactionTypes", typeof(TuraVerifyReaction));
    }
}
