using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fusion.Element.Database.Suite;

public class DatabaseElementRegistrar : IElementRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<MySqlDatabaseElement>();
        services.AddTransient<MsSqlDatabaseElement>();
        services.AddTransient<PostgreSqlDatabaseElement>();
        services.AddTransient<DuckDbDatabaseElement>();
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
                    "DUCKDB" => ActivatorUtilities.CreateInstance<DuckDbDatabaseElement>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<MsSqlDatabaseElement>(provider, config, logger)
                };
            });
    }
}
