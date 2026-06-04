using Blueprints;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Database.Suite;

public class DatabaseElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<MySqlDatabaseElement>();
        services.AddTransient<MsSqlDatabaseElement>();
        services.AddTransient<PostgreSqlDatabaseElement>();
        services.AddTransient<DatabasePruningElement>();

        // Register the factory for IDatabaseElement
        services.AddTransient<Func<IElementBlueprint, IFireLogger, IDatabaseElement>>(provider => 
            (config, logger) => 
            {
                var dbType = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "DatabaseType", "MsSql");

                return dbType.ToUpper() switch
                {
                    "PRUNING" => ActivatorUtilities.CreateInstance<DatabasePruningElement>(provider, config, logger),
                    "MYSQL" => ActivatorUtilities.CreateInstance<MySqlDatabaseElement>(provider, config, logger),
                    "POSTGRESQL" => ActivatorUtilities.CreateInstance<PostgreSqlDatabaseElement>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<MsSqlDatabaseElement>(provider, config, logger)
                };
            });
    }
}
