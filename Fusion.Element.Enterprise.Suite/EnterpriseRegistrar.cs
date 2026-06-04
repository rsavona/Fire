using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Fusion.Element.Enterprise.Suite.Redis;
using Fusion.Element.Enterprise.Suite.Telemetry;
using Fusion.Element.Enterprise.Suite.Inventory;
using Fusion.Element.Enterprise.Suite.Logging;

namespace Fusion.Element.Enterprise.Suite;

public class EnterpriseRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        // Redis
        services.AddSingleton<RedisCacheManager>();
        services.AddTransient<RedisCacheElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, RedisCacheElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<RedisCacheElement>(provider, config, logger));

        // Telemetry
        services.AddSingleton<TelemetryManager>();
        services.AddTransient<TelemetryElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, TelemetryElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<TelemetryElement>(provider, config, logger));

        // Inventory
        services.AddSingleton<InventoryManager>();
        services.AddTransient<InventoryElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, InventoryElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<InventoryElement>(provider, config, logger));

        // Centralized Logging
        services.AddSingleton<FireLogManager>();
        services.AddTransient<FireLogElement>();
        services.AddTransient<Func<IElementBlueprint, IFireLogger, FireLogElement>>(provider => 
            (config, logger) => ActivatorUtilities.CreateInstance<FireLogElement>(provider, config, logger));
    }
}
