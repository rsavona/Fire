
using System.Collections.Concurrent;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;


namespace DeviceSpace.Core;

/// <summary>
/// The main background service for the Fortna FIRE application.
/// This service is the core orchestrator. It starts the plugin manager
/// and acts as the central status aggregator.
/// </summary>
public class DeviceSpaceCore : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<DeviceSpaceCore> _logger;
    
    // This dictionary now stores the *summary string* from each manager.
    // Key: Manager Name (e.g., "PlcDeviceManager")
    // Value: Summary String (e.g., "PLC1: Connected, PLC2: Faulted")
    private readonly ConcurrentDictionary<string, string> _managerStatusSummaries = new();
 
    public DeviceSpaceCore(
        IMessageBus messageBus,
        ILogger<DeviceSpaceCore> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    /// <summary>
    /// This method is called when the IHostedService starts.
    /// It starts the plugin manager and subscribes to the central status queue.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
       
       try
       {
            await Task.Delay(Timeout.Infinite, stoppingToken);
       }
       catch (OperationCanceledException)
       {
            _logger.LogInformation("DeviceSpaceCore is stopping.");
       }
       catch (Exception ex)
       {
            _logger.LogCritical(ex, "DeviceSpaceCore encountered a fatal error.");
       }
    }
    
    /// <summary>
    /// Thread-safe method to update the master status list.
    /// </summary>
    private void AddOrUpdateDeviceData(string managerName, string summary)
    {
        if (string.IsNullOrEmpty(managerName))
        {
            return;
        }
        _managerStatusSummaries[managerName] = summary;
    }

    /// <summary>
    /// (Optional) For external services to query the current state.
    /// </summary>
    public ConcurrentDictionary<string, string> GetAllStatuses()
    {
        return new ConcurrentDictionary<string, string>(_managerStatusSummaries);
    }

    /// <summary>
    /// Called when the application is shutting down.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DeviceSpaceCore shutting down...");

        await base.StopAsync(cancellationToken);
    }
}

