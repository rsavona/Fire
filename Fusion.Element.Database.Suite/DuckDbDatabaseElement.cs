using System.Data;
using Dapper;
using DuckDB.NET.Data;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;

namespace Fusion.Element.Database.Suite;

/// <summary>
/// In-process DuckDB database element. Supports file-backed databases
/// (a "Database" or "ConnectionString" property pointing at a .duckdb file)
/// and in-memory databases (":memory:").
/// A single long-lived connection is maintained so that in-memory databases
/// keep their contents between operations.
/// </summary>
public class DuckDbDatabaseElement : DatabaseElementBase
{
    private readonly string _resolvedConnectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private DuckDBConnection? _connection;

    public DuckDbDatabaseElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger)
        : base(bus, config, logger)
    {
        var database = ConfigurationLoader.GetOptionalConfig(config.Properties, "Database", string.Empty);
        _resolvedConnectionString = ResolveConnectionString(ConnectionString, database);
    }

    /// <summary>
    /// Accepts a full ADO.NET connection string ("DataSource=..."), a bare file path,
    /// or ":memory:". Defaults to an in-memory database when nothing is configured.
    /// </summary>
    private static string ResolveConnectionString(string connectionString, string database)
    {
        var raw = !string.IsNullOrWhiteSpace(connectionString) ? connectionString : database;
        if (string.IsNullOrWhiteSpace(raw)) raw = ":memory:";

        return raw.Contains("DataSource", StringComparison.OrdinalIgnoreCase)
            ? raw
            : $"DataSource={raw}";
    }

    public override async Task InitializeDatabaseAsync()
    {
        try
        {
            // DuckDB is in-process: opening the connection creates the database file
            // (or the in-memory instance) if it does not exist yet.
            var connection = await GetOpenConnectionAsync(ConnectionToken);
            Logger.Information("[{Element}] DuckDB Database '{Db}' verified/created.", Config.Name, connection.DataSource);

            // Run Table Scripts (Placeholder for schema logic)
            // await connection.ExecuteAsync("...");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] DuckDB Dynamic Initialization Failed.", Config.Name);
            throw;
        }
    }

    protected override async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            Logger.Debug("[{Element}] Attempting to connect to DuckDB...", Config.Name);
            await GetOpenConnectionAsync(ct);
            Logger.Information("[{Element}] DuckDB Connection Successful.", Config.Name);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Element}] DuckDB Connection Failed.", Config.Name);
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
            Logger.Debug("[{Element}] Executing DuckDB Query: {Sql} (Type={Type})", Config.Name, sql, commandType);

        await _connectionLock.WaitAsync(ConnectionToken);
        try
        {
            if (track) Tracker.IncrementInbound();
            var connection = await GetOpenConnectionAsync(ConnectionToken);

            if (track) StartTransaction("", 1);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));

            if (track)
            {
                var tm = StopTransaction("", 1);
                Logger.Information("[{Element}] DuckDB Query Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("DuckDB Query Failed", ex);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public override async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, bool track = true)
    {
        if (track)
            Logger.Debug("[{Element}] Executing DuckDB Command: {Sql} (Type={Type})", Config.Name, sql, commandType);

        await _connectionLock.WaitAsync(ConnectionToken);
        try
        {
            if (track) Tracker.IncrementInbound();
            var connection = await GetOpenConnectionAsync(ConnectionToken);

            if (track) StartTransaction("", 2);
            var result = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, commandType: commandType, cancellationToken: ConnectionToken));

            if (track)
            {
                var tm = StopTransaction("", 2);
                Logger.Information("[{Element}] DuckDB Command Successful. {tm}ms", Config.Name, tm);
                Tracker.IncrementOutbound();
            }

            await NotifyHeartbeatReceived(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            if (track) OnError("DuckDB Command Failed", ex);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Returns the shared connection, (re)opening it if needed. DuckDB in-memory
    /// databases are scoped to a connection, so we keep one alive for the element.
    /// </summary>
    private async Task<DuckDBConnection> GetOpenConnectionAsync(CancellationToken ct)
    {
        if (_connection is { State: ConnectionState.Open })
            return _connection;

        _connection?.Dispose();
        _connection = new DuckDBConnection(_resolvedConnectionString);
        await _connection.OpenAsync(ct);
        return _connection;
    }

    protected override Task OnElementStoppingAsync()
    {
        CloseConnection();
        return base.OnElementStoppingAsync();
    }

    protected override void DisposeManagedResources()
    {
        CloseConnection();
        _connectionLock.Dispose();
        base.DisposeManagedResources();
    }

    private void CloseConnection()
    {
        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warning("[{Element}] Error closing DuckDB connection: {Msg}", Config.Name, ex.Message);
        }
        finally
        {
            _connection = null;
        }
    }
}
