using System.Data;
using Dapper;
using Fusion.Common.Contracts;
using Npgsql;

namespace Fusion.Element.Database.Suite;

public class PostgreSqlDatabaseElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
    : DatabaseElementBase(bus, config, logger)
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
                Logger.Information("[{Element}] PostgreSQL Database '{Db}' created.", Config.Name, targetDb);
            }
            else
            {
                Logger.Information("[{Element}] PostgreSQL Database '{Db}' already exists.", Config.Name, targetDb);
            }

            // 2. Run Table Scripts (Placeholder for schema logic)
            // await using var targetConn = new NpgsqlConnection(ConnectionString);
            // await targetConn.OpenAsync();
            // await targetConn.ExecuteAsync("...");

        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] PostgreSQL Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Element}] Attempting to connect to PostgreSQL...", Config.Name);
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Element}] PostgreSQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] PostgreSQL Connection Failed.", Config.Name);
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
            Logger.Debug("[{Element}] Executing PostgreSQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
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
                Logger.Information("[{Element}] PostgreSQL Query Successful. {tm}ms", Config.Name, tm);
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
            Logger.Debug("[{Element}] Executing PostgreSQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
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
                Logger.Information("[{Element}] PostgreSQL Command Successful. {tm}ms", Config.Name, tm);
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
