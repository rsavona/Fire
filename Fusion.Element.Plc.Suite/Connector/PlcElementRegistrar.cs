using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

// Namespace where your PLC element lives

namespace Fusion.Element.Plc.Suite.Connector;

public class PlcElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // 1. Standard PLC Server
        services.AddTransient<PlcServerElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, PlcServerElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<PlcServerElement>(provider, config, logger);
            });

        // 2. Allen-Bradley CIP PLC
        services.AddTransient<AbCipPlcElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, AbCipPlcElement>>(provider => 
            (config, logger) => 
            {
                return ActivatorUtilities.CreateInstance<AbCipPlcElement>(provider, config, logger);
            });
    }
}
