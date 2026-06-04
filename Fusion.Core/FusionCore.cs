
using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;


namespace Fusion.Core;

/// <summary>
/// The main background service for the Fortna FIRE application.
/// This service is the core orchestrator. It starts the plugin manager
/// and acts as the central status aggregator.
/// </summary>
public class FusionCore : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<FusionCore> _logger;
    
    // This dictionary now stores the *summary string* from each manager.
    // Key: Manager CustomerName (e.g., "PlcElementManager")
    // Value: Summary String (e.g., "PLC1: Connected, PLC2: Faulted")
    private readonly ConcurrentDictionary<string, string> _managerStatusSummaries = new();
    private readonly ConcurrentDictionary<string, ElementHealth> _elementHealths = new();
    private bool _systemStarted = false;
    private int _expectedElementCount = 0;

    public FusionCore(
        IMessageBus messageBus,
        ILogger<FusionCore> logger)
    {
        _messageBus = messageBus;
        _logger = logger;

        _expectedElementCount = ConfigurationLoader.GetAllElementConfig().Count(d => d.Enable);
    }

    /// <summary>
    /// This method is called when the IHostedService starts.
    /// It starts the plugin manager and subscribes to the central status queue.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
       await Task.Yield();
       _logger.LogInformation("Orchestrator: Monitoring {Count} elements for system ready.", _expectedElementCount);

       // Publish System Topology for Dashboards
       try
       {
           var topology = new SystemTopologyMessage
           {
               Elements = ConfigurationLoader.GetAllElementConfig().Where(d => d.Enable).ToList(),
               Reactions = ConfigurationLoader.GetAllReactionConfig().Where(w => w.Enable).Cast<IReactionBlueprint>().ToList(),
               SystemName = ConfigurationLoader.GetSpaceConfig()?.CustomerName ?? "Fusion"
           };
           await _messageBus.PublishAsync(MessageBusTopic.SystemTopology.ToString(), 
               new MessageEnvelope(MessageBusTopic.SystemTopology, topology));
       }
       catch (Exception ex)
       {
           _logger.LogWarning(ex, "Orchestrator: Failed to publish system topology.");
       }
       
       // Subscribe to element status to track system readiness
       await _messageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), HandleStatusUpdateAsync);

       try
       {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_systemStarted && CheckSystemReadiness())
                {
                    _logger.LogInformation("Orchestrator: ALL ELEMENTS READY. Releasing System START signal.");
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
            _logger.LogInformation("FusionCore is stopping.");
       }
       catch (Exception ex)
       {
            _logger.LogCritical(ex, "FusionCore encountered a fatal error.");
       }
    }

    private Task HandleStatusUpdateAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is IElementStatus status)
        {
            // Use the actual element name from the status message, 
            // not the generic "All_Elements" topic name from the envelope destination.
            _elementHealths[status.ElementId.ElementName] = status.Health;
        }
        return Task.CompletedTask;
    }

    private bool CheckSystemReadiness()
    {
        if (_expectedElementCount == 0) return true;
        if (_elementHealths.Count < _expectedElementCount) return false;

        return _elementHealths.Values.All(h => h == ElementHealth.Normal || h == ElementHealth.Warning);
    }
    
    /// <summary>
    /// Thread-safe method to update the master status list.
    /// </summary>
    private void AddOrUpdateElementData(string managerName, string summary)
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
        _logger.LogInformation("FusionCore shutting down...");

        await base.StopAsync(cancellationToken);
    }
    
    public IEnumerable<DiagCommand> GetAvailableCommands()
    {
        yield return new DiagCommand("ListSubscriptions",
            "Returns a list of all active topics and their handlers on the MessageBus.");
    }
}

