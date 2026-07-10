using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Sparkplug;

/// <summary>
/// Registers the SparkplugEdgeNodeElement and its factory delegate with the DI
/// container. Discovered automatically by the Fusion.Element.*.dll scan.
/// </summary>
public class SparkplugElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<SparkplugEdgeNodeElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, SparkplugEdgeNodeElement>>(provider =>
            (config, logger) =>
                ActivatorUtilities.CreateInstance<SparkplugEdgeNodeElement>(provider, config, logger));
    }
}
