using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Logger;

public class LogSinkElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<SerilogSinkElement>();

        services.AddTransient<Func<IElementBlueprint, IFireLogger, SerilogSinkElement>>(provider =>
            (config, logger) =>
                ActivatorUtilities.CreateInstance<SerilogSinkElement>(provider, config, logger));
    }
}
