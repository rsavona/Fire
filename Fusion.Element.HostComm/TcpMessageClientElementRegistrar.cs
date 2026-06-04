using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.HostComm;

/// <summary>
/// Registers the TcpMessageClientElement and its associated manager with the DI container.
/// </summary>
public class TcpMessageClientElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the element type
        services.AddTransient<TcpMessageClientElement>();

        // 2. Register the Factory Delegate for the Manager
        services.AddTransient<Func<IElementBlueprint, IFireLogger, TcpMessageClientElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<TcpMessageClientElement>(provider, config, logger);
            });
    }
}
