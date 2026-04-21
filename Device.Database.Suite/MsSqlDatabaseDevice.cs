using System.Data;
using Dapper;
using DeviceSpace.Common.Contracts;
using Microsoft.Data.SqlClient;

namespace Device.Database.Suite;

public class MsSqlDatabaseDevice(IDeviceConfig config, IFireLogger logger) 
    : DatabaseDeviceBase(config, logger)
{
    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Device}] Attempting to connect to MSSQL...", Config.Name);
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Device}] MSSQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Device}] MSSQL Connection Failed.", Config.Name);
            return false;
        }
    }

    public override async Task SendHeartbeatAsync(CancellationToken token)
    {
        await ExecuteAsync("SELECT 1");
    }

    public override async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text)
    {
        Logger.Debug("[{Device}] Executing MSSQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            Tracker.IncrementInbound();
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);
            StartTransaction("", 1);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            var tm = StopTransaction("", 1);
            Logger.Information("[{Device}] MSSQL Query Successful. {tm}ms", Config.Name, tm);

            Tracker.IncrementOutbound();
            await NotifyHeartbeatReceived(this, EventArgs.Empty); 
            return result;
        }
        catch (Exception ex)
        {
            OnError("MSSQL Query Failed", ex);
            return null;
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text)
    {
        Logger.Debug("[{Device}] Executing MSSQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            OnError("MSSQL Command Failed", ex);
            throw;
        }
    }
}
