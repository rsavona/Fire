using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.ActiveMQ;

public class ActiveMqRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Register the element type
        services.AddTransient<ActiveMqElement>();
        services.AddTransient<ActiveMqBrowserElement>();
        services.AddTransient<ActiveMqQueuePeekElement>();
       
        services.AddTransient<Func<IElementBlueprint, IFireLogger, ActiveMqElement>>(provider =>
            (config, logger) =>
            {
                // Now it only needs config and logger
                return ActivatorUtilities.CreateInstance<ActiveMqElement>(provider, config, logger);
            });

        services.AddTransient<Func<IElementBlueprint, IFireLogger, ActiveMqBrowserElement>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<ActiveMqBrowserElement>(provider, config, logger);
            });

        services.AddTransient<Func<IElementBlueprint, IFireLogger, ActiveMqQueuePeekElement>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<ActiveMqQueuePeekElement>(provider, config, logger);
            });
    }
}