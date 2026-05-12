using System.Data;
using Dapper;
using DeviceSpace.Common.Contracts;
using Npgsql;

namespace Device.Database.Suite;

public class PostgreSqlDatabaseDevice(IMessageBus bus, IDeviceConfig config, IFireLogger logger) 
    : DatabaseDeviceBase(bus, config, logger)
{
    public override async Task InitializeDatabaseAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            var targetDb = builder.Database;
            
            // Connect to maintenance database to create target
            builder.Database = "postgres"; 
            await using var maintenanceConn = new NpgsqlConnection(builder.ConnectionString);
            await maintenanceConn.OpenAsync();

            // 1. Create Database if not exists
            var checkDbSql = $"SELECT 1 FROM pg_database WHERE datname = '{targetDb}'";
            var exists = await maintenanceConn.ExecuteScalarAsync<int?>(checkDbSql);
            
            if (exists != 1)
            {
                var createDbSql = $"CREATE DATABASE \"{targetDb}\"";
                await maintenanceConn.ExecuteAsync(createDbSql);
                Logger.Information("[{Device}] PostgreSQL Database '{Db}' created.", Config.Name, targetDb);
            }
            else
            {
                Logger.Information("[{Device}] PostgreSQL Database '{Db}' already exists.", Config.Name, targetDb);
            }

            // 2. Run Table Scripts (Placeholder for schema logic)
            // await using var targetConn = new NpgsqlConnection(ConnectionString);
            // await targetConn.OpenAsync();
            // await targetConn.ExecuteAsync("...");

        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Device}] PostgreSQL Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Device}] Attempting to connect to PostgreSQL...", Config.Name);
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Device}] PostgreSQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Device}] PostgreSQL Connection Failed.", Config.Name);
            return false;
        }
    }

    public override async Task SendHeartbeatAsync(CancellationToken token)
    {
        await ExecuteAsync("SELECT 1", track: false);
    }

    public override async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true)
    {
        if (track)
            Logger.Debug("[{Device}] Executing PostgreSQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 1);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 1);
                Logger.Information("[{Device}] PostgreSQL Query Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("PostgreSQL Query Failed", ex);
            throw;
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true)
    {
        if (track)
            Logger.Debug("[{Device}] Executing PostgreSQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 2);
            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 2);
                Logger.Information("[{Device}] PostgreSQL Command Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("PostgreSQL Command Failed", ex);
            throw;
        }
    }
}
