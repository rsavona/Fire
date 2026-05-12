using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeviceSpace.Core;

/// <summary>
/// Manages the lifecycle of all workflows, supporting hot-reloading when configuration changes.
/// </summary>
public class WorkflowOrchestrator : BackgroundService
{
    private readonly IWorkflowFactory _workflowFactory;
    private readonly ILogger<WorkflowOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, IHostedService> _runningWorkflows = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationToken _stoppingToken;

    public WorkflowOrchestrator(IWorkflowFactory workflowFactory, ILogger<WorkflowOrchestrator> logger)
    {
        _workflowFactory = workflowFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stoppingToken = stoppingToken;
        _logger.LogInformation("WorkflowOrchestrator started. Initializing workflows...");

        await ReconcileWorkflowsAsync();

        // Subscribe to configuration changes
        ConfigurationLoader.OnConfigurationChanged += async () =>
        {
            _logger.LogInformation("Configuration change detected. Reconciling workflows...");
            await ReconcileWorkflowsAsync();
        };

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ReconcileWorkflowsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var configs = ConfigurationLoader.GetAllWorkflowConfig();
            var activeConfigNames = configs.Where(c => c.Enable).Select(c => c.Name).ToHashSet();

            // 1. Stop Removed or Disabled Workflows
            var toRemove = _runningWorkflows.Keys.Where(name => !activeConfigNames.Contains(name)).ToList();
            foreach (var name in toRemove)
            {
                if (_runningWorkflows.TryRemove(name, out var workflow))
                {
                    _logger.LogWarning("[{Workflow}] Stopping workflow (removed or disabled)...", name);
                    await workflow.StopAsync(CancellationToken.None);
                }
            }

            // 2. Start or Update Workflows
            foreach (var config in configs.Where(c => c.Enable))
            {
                if (_runningWorkflows.TryGetValue(config.Name, out var existing))
                {
                    // For now, if any part of workflow config changes, we restart it.
                    // We can refine this later to check specific property changes if needed.
                    _logger.LogInformation("[{Workflow}] Restarting workflow for potential configuration update...", config.Name);
                    await existing.StopAsync(CancellationToken.None);
                    
                    var updated = _workflowFactory.CreateWorkflow(config);
                    _runningWorkflows[config.Name] = updated;
                    await updated.StartAsync(_stoppingToken);
                }
                else
                {
                    _logger.LogInformation("[{Workflow}] Starting new workflow...", config.Name);
                    var workflow = _workflowFactory.CreateWorkflow(config);
                    if (_runningWorkflows.TryAdd(config.Name, workflow))
                    {
                        await workflow.StartAsync(_stoppingToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconciling workflows.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WorkflowOrchestrator stopping. Shutting down all workflows...");
        foreach (var workflow in _runningWorkflows.Values)
        {
            await workflow.StopAsync(cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
