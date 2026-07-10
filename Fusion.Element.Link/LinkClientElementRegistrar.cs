using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Link;

/// <summary>
/// Registers the LinkClientElement and its factory delegate with the DI container.
/// </summary>
public class LinkClientElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<LinkClientElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, LinkClientElement>>(provider =>
            (config, logger) =>
                ActivatorUtilities.CreateInstance<LinkClientElement>(provider, config, logger));
    }
}
