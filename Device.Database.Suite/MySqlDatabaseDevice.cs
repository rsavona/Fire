using System.Data;
using Dapper;
using DeviceSpace.Common.Contracts;
using MySqlConnector;

namespace Device.Database.Suite;

public class MySqlDatabaseDevice(IDeviceConfig config, IFireLogger logger) 
    : DatabaseDeviceBase(config, logger)
{
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
        await ExecuteAsync("SELECT 1");
    }

    public override async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text)
    {
        Logger.Debug("[{Device}] Executing MySQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            OnError("MySQL Query Failed", ex);
            throw;
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text)
    {
        Logger.Debug("[{Device}] Executing MySQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            OnError("MySQL Command Failed", ex);
            throw;
        }
    }
}
