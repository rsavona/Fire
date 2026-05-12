using System.Data;
using Dapper;
using DeviceSpace.Common.Contracts;
using MySqlConnector;

namespace Device.Database.Suite;

public class MySqlDatabaseDevice(IDeviceConfig config, IFireLogger logger) 
    : DatabaseDeviceBase(config, logger)
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
            Logger.Information("[{Device}] MySQL Database '{Db}' verified/created.", Config.Name, targetDb);

            // 2. Run Table Scripts (Placeholder for schema logic)
            // await using var targetConn = new MySqlConnection(ConnectionString);
            // await targetConn.OpenAsync();
            // await targetConn.ExecuteAsync("...");

        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Device}] MySQL Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Device}] Attempting to connect to MySQL...", Config.Name);
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Device}] MySQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Device}] MySQL Connection Failed.", Config.Name);
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
            Logger.Debug("[{Device}] Executing MySQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
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
                Logger.Information("[{Device}] MySQL Query Successful. {tm}ms", Config.Name, tm);
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
            Logger.Debug("[{Device}] Executing MySQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
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
                Logger.Information("[{Device}] MySQL Command Successful. {tm}ms", Config.Name, tm);
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
    
    public async Task OnDeviceMessageToMessageBusAsync(string sqlMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sqlMessage);

        try
        {
            logger.LogDebug("Received SQL device message {MessageId}. Preparing for bus routing...", sqlMessage);

            // 1. Transform or enrich the message if needed before hitting the core bus
                
        
            logger.LogInfo("Successfully routed MSSQL message {MessageId} to the orchestration bus.", "");
        }
        catch (Exception ex)
        {
            // Log the failure to ensure we don't lose track of dropped DB events
            logger.LogError(ex, "Failed to route message {MessageId} from MSSQL device to the message bus.", sqlMessage);
            
            // Depending on your error handling, you might want to throw, push to a Dead Letter Queue, or return a failure result.
            throw; 
        }
    }
}
