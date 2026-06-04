using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fusion.Core;

/// <summary>
/// Manages the lifecycle of all reactions, supporting hot-reloading when configuration changes.
/// </summary>
public class ReactionOrchestrator : BackgroundService
{
    private readonly IReactionFactory _reactionFactory;
    private readonly ILogger<ReactionOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, IHostedService> _runningReactions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationToken _stoppingToken;

    public ReactionOrchestrator(IReactionFactory reactionFactory, ILogger<ReactionOrchestrator> logger)
    {
        _reactionFactory = reactionFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stoppingToken = stoppingToken;
        _logger.LogInformation("ReactionOrchestrator started. Initializing reactions...");

        await ReconcileReactionsAsync();

        // Subscribe to configuration changes
        ConfigurationLoader.OnConfigurationChanged += async () =>
        {
            _logger.LogInformation("Configuration change detected. Reconciling reactions...");
            await ReconcileReactionsAsync();
        };

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ReconcileReactionsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var configs = ConfigurationLoader.GetAllReactionConfig();
            var activeConfigNames = configs.Where(c => c.Enable).Select(c => c.Name).ToHashSet();

            // 1. Stop Removed or Disabled Reactions
            var toRemove = _runningReactions.Keys.Where(name => !activeConfigNames.Contains(name)).ToList();
            foreach (var name in toRemove)
            {
                if (_runningReactions.TryRemove(name, out var reaction))
                {
                    _logger.LogWarning("[{Reaction}] Stopping reaction (removed or disabled)...", name);
                    await reaction.StopAsync(CancellationToken.None);
                }
            }

            // 2. Start or Update Reactions
            foreach (var config in configs.Where(c => c.Enable))
            {
                if (_runningReactions.TryGetValue(config.Name, out var existing))
                {
                    // For now, if any part of reaction config changes, we restart it.
                    // We can refine this later to check specific property changes if needed.
                    _logger.LogInformation("[{Reaction}] Restarting reaction for potential configuration update...", config.Name);
                    await existing.StopAsync(CancellationToken.None);
                    
                    var updated = _reactionFactory.CreateReaction(config);
                    _runningReactions[config.Name] = updated;
                    await updated.StartAsync(_stoppingToken);
                }
                else
                {
                    _logger.LogInformation("[{Reaction}] Starting new reaction...", config.Name);
                    var reaction = _reactionFactory.CreateReaction(config);
                    if (_runningReactions.TryAdd(config.Name, reaction))
                    {
                        await reaction.StartAsync(_stoppingToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconciling reactions.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ReactionOrchestrator stopping. Shutting down all reactions...");
        foreach (var reaction in _runningReactions.Values)
        {
            await reaction.StopAsync(cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
