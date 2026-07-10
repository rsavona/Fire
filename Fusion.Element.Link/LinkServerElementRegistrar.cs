using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Link;

/// <summary>
/// Registers the LinkServerElement and its factory delegate with the DI container.
/// </summary>
public class LinkServerElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<LinkServerElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, LinkServerElement>>(provider =>
            (config, logger) =>
                ActivatorUtilities.CreateInstance<LinkServerElement>(provider, config, logger));
    }
}
