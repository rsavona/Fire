
using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Messaging;
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
    private readonly ConcurrentDictionary<string, DeviceHealth> _deviceHealths = new();
    private bool _systemStarted = false;
    private int _expectedDeviceCount = 0;

    public DeviceSpaceCore(
        IMessageBus messageBus,
        ILogger<DeviceSpaceCore> logger)
    {
        _messageBus = messageBus;
        _logger = logger;

        _expectedDeviceCount = ConfigurationLoader.GetAllDeviceConfig().Count(d => d.Enable);
    }

    /// <summary>
    /// This method is called when the IHostedService starts.
    /// It starts the plugin manager and subscribes to the central status queue.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
       _logger.LogInformation("Orchestrator: Monitoring {Count} devices for system ready.", _expectedDeviceCount);
       
       // Subscribe to device status to track system readiness
       await _messageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleStatusUpdateAsync);

       try
       {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_systemStarted && CheckSystemReadiness())
                {
                    _logger.LogInformation("Orchestrator: ALL DEVICES READY. Releasing System START signal.");
                    _systemStarted = true;
                    var startMsg = new SystemControlMessage(SystemCommand.Start);
                    await _messageBus.PublishAsync(MessageBusTopic.SystemControl.ToString(), 
                        new MessageEnvelope(MessageBusTopic.SystemControl, startMsg));
                }

                await Task.Delay(1000, stoppingToken);
            }
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

    private Task HandleStatusUpdateAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is IDeviceStatus status)
        {
            _deviceHealths[envelope.Destination.DeviceName] = status.Health;
        }
        return Task.CompletedTask;
    }

    private bool CheckSystemReadiness()
    {
        if (_expectedDeviceCount == 0) return true;
        if (_deviceHealths.Count < _expectedDeviceCount) return false;

        return _deviceHealths.Values.All(h => h == DeviceHealth.Normal || h == DeviceHealth.Warning);
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
    
    public IEnumerable<DiagCommand> GetAvailableCommands()
    {
        yield return new DiagCommand("ListSubscriptions",
            "Returns a list of all active topics and their handlers on the MessageBus.");
    }
}

