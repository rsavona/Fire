using System.Data;
using Blueprints;
using Fusion.Common.Contracts;

namespace Device.Database.Suite;

/// <summary>
/// A specialized element for database maintenance tasks like pruning old records.
/// </summary>
public class DatabasePruningElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger) 
    : MsSqlDatabaseElement(bus, config, logger)
{
    private readonly int _retentionDays = ConfigurationLoader.GetOptionalConfig(config.Properties, "RetentionDays", 7);
    private readonly string[] _tables = ConfigurationLoader.GetOptionalConfig(config.Properties, "Tables", "Conveyable,ConveyableDestination,MessageQueue,SystemEvents")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override async Task SendHeartbeatAsync(CancellationToken token)
    {
        // Hijack the heartbeat loop to perform pruning. 
        // The interval is controlled by HeartbeatIntervalMs in the config.
        await base.SendHeartbeatAsync(token);
        await PruneRecordsAsync(token);
    }

    private async Task PruneRecordsAsync(CancellationToken token)
    {
        Logger.Information("[{Dev}] Starting database pruning task (Retention: {Days} days)...", Config.Name, _retentionDays);

        foreach (var table in _tables)
        {
            if (token.IsCancellationRequested) break;

            try
            {
                // Determine the timestamp column (heuristic or config)
                string column = table.Equals("HostCommLog", StringComparison.OrdinalIgnoreCase) ? "LoggedTime" : "CreatedTime";
                
                // Check if we should use Partitioning (if configured)
                bool usePartitioning = ConfigurationLoader.GetOptionalConfig(Config.Properties, "UsePartitioning", false);

                if (usePartitioning)
                {
                    await MaintainPartitionsAsync(table, token);
                }
                else
                {
                    string sql = $"DELETE FROM {table} WHERE {column} < DATEADD(day, -@{nameof(_retentionDays)}, GETDATE())";
                    int deleted = await ExecuteAsync(sql, new { _retentionDays }, track: false);
                    
                    if (deleted > 0)
                        Logger.Information("[{Dev}] Pruned {Count} records from {Table}.", Config.Name, deleted, table);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Failed to prune table {Table}.", Config.Name, table);
            }
        }
    }

    private async Task MaintainPartitionsAsync(string table, CancellationToken token)
    {
        // This is a placeholder for more advanced partition management logic
        // For now, it just logs that it would use partitions.
        Logger.Debug("[{Dev}] Partition maintenance for {Table} is not yet fully implemented.", Config.Name, table);
        
        // Example: TRUNCATE TABLE Conveyable WITH (PARTITIONS (1 TO 2))
        // But we need to know which partitions are old.
    }
}
