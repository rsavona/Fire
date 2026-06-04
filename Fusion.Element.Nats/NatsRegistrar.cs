using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Fusion.Common.Logging;

namespace Fusion.Element.Nats;

public class NatsRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<NatsElement>();
        services.AddSingleton<NatsManager>();
       
        services.AddTransient<Func<IElementBlueprint, IFireLogger, NatsElement>>(provider =>
            (config, logger) =>
            {
                return ActivatorUtilities.CreateInstance<NatsElement>(provider, config, logger);
            });
    }
}
