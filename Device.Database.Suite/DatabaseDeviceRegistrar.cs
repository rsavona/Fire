using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Device.Database.Suite;

public class DatabaseDeviceRegistrar : IDeviceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddTransient<MySqlDatabaseDevice>();
        services.AddTransient<MsSqlDatabaseDevice>();
        services.AddTransient<PostgreSqlDatabaseDevice>();

        // Register the factory for IDatabaseDevice
        services.AddTransient<Func<IDeviceConfig, IFireLogger, IDatabaseDevice>>(provider => 
            (config, logger) => 
            {
                var dbType = ConfigurationLoader.GetOptionalConfig<string>(config.Properties, "DatabaseType", "MsSql");

                return dbType.ToUpper() switch
                {
                    "MYSQL" => ActivatorUtilities.CreateInstance<MySqlDatabaseDevice>(provider, config, logger),
                    "POSTGRESQL" => ActivatorUtilities.CreateInstance<PostgreSqlDatabaseDevice>(provider, config, logger),
                    _ => ActivatorUtilities.CreateInstance<MsSqlDatabaseDevice>(provider, config, logger)
                };
            });
    }
}
