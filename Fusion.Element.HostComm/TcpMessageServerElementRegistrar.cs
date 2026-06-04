using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.HostComm;

/// <summary>
/// Registers the TcpMessageServerElement and its associated manager with the DI container.
/// </summary>
public class TcpMessageServerElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the element type
        services.AddTransient<TcpMessageServerElement>();

        // 2. Register the Factory Delegate for the Manager
        services.AddTransient<Func<IElementBlueprint, IFireLogger, TcpMessageServerElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<TcpMessageServerElement>(provider, config, logger);
            });

        // 3. The Manager itself will be discovered by type scanning in CoreServicesExtensions
        // if it inherits from IElementManager (via ElementManagerBase) and is in the assembly.
    }
}
