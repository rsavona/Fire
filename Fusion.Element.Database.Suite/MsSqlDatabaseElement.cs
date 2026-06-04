using System.Data;
using Dapper;
using Fusion.Common.Contracts;
using Microsoft.Data.SqlClient;

namespace Fusion.Element.Database.Suite;

public class MsSqlDatabaseElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
    : DatabaseElementBase(bus, config, logger)
{
    public override async Task InitializeDatabaseAsync()
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString);
            var targetDb = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            await using var masterConn = new SqlConnection(builder.ConnectionString);
            await masterConn.OpenAsync();

            // 1. Create Database if not exists
            var checkDbSql = $"IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'{targetDb}') CREATE DATABASE [{targetDb}]";
            await masterConn.ExecuteAsync(checkDbSql);
            Logger.Information("[{Element}] MSSQL Database '{Db}' verified/created.", Config.Name, targetDb);

            // 2. Run Table Scripts (Placeholder for schema logic)
            // This is where you will add your table creation SQL
            // await using var targetConn = new SqlConnection(ConnectionString);
            // await targetConn.OpenAsync();
            // await targetConn.ExecuteAsync("..."); 

        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] MSSQL Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Element}] Attempting to connect to MSSQL...", Config.Name);
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ct);
            Logger.Information("[{Element}] MSSQL Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] MSSQL Connection Failed.", Config.Name);
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
            Logger.Debug("[{Element}] Executing MSSQL Query: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 1);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 1);
                Logger.Information("[{Element}] MSSQL Query Successful. {tm}ms  {result}", Config.Name, tm, result);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty); 
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("MSSQL Query Failed", ex);
            throw;
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true)
    {
        if (track)
            Logger.Debug("[{Element}] Executing MSSQL Command: {Sql} (Type={Type})", Config.Name, sql, commandType);
        
        try
        {
            if (track) Tracker.IncrementInbound();
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(ConnectionToken);

            if (track) StartTransaction("", 2);
            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));
            
            if (track)
            {
                var tm = StopTransaction("", 2);
                Logger.Information("[{Element}] MSSQL Command Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("MSSQL Command Failed", ex);
            throw;
        }
    }
}
