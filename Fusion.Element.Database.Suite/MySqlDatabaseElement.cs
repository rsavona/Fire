using System.Data;
using Dapper;
using Fusion.Common.Contracts;
using MySqlConnector;

namespace Fusion.Element.Database.Suite;

public class MySqlDatabaseElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
    : DatabaseElementBase(bus, config, logger)
{
    public override async Task InitializeDatabaseAsync()
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(ConnectionString);
            var targetDb = builder.Database;
            
            // Connect without database to create it
            builder.Database = ""; 
            await using var serverConn = new MySqlConnection(builder.ConnectionString);
            await serverConn.OpenAsync();

            // 1. Create Database if not exists
            var createDbSql = $"CREATE DATABASE IF NOT EXISTS `{targetDb}`";
            await serverConn.ExecuteAsync(createDbSql);
            Logger.Information("[{Element}] MySQL Database '{Db}' verified/created.", Config.Name, targetDb);

            // 2. Run Table Scripts (Placeholder for schema logic)
            // await using var targetConn = new MySqlConnection(ConnectionString);
            // await targetConn.OpenAsync();
            // await targetConn.ExecuteAsync("...");

        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] MySQL Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Element}] Attempting to connect to MySQL...", Config.Name);
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Element}] MySQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] MySQL Connection Failed.", Config.Name);
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
            Logger.Debug("[{Element}] Executing MySQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 1);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 1);
                Logger.Information("[{Element}] MySQL Query Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("MySQL Query Failed", ex);
            throw;
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true)
    {
        if (track)
            Logger.Debug("[{Element}] Executing MySQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 2);
            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 2);
                Logger.Information("[{Element}] MySQL Command Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("MySQL Command Failed", ex);
            throw;
        }
    }
}
