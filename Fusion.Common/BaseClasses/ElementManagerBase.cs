using System.Collections.Concurrent;
using System.Reflection;
using Fusion.Common.Attributes;
using Fusion.Common.Blueprints;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Fusion.Common.BaseClasses;

/// <summary>
/// Base class for Element Managers.
/// </summary>
/// <typeparam name="TElement"></typeparam>
public abstract class ElementManagerBase<TElement> : BackgroundService, IElementManager
    where TElement : IElement
{
    protected readonly IMessageBus MessageBus;
    protected readonly List<IElementBlueprint> ElementConfigList;
    protected readonly IFireLogger<ElementManagerBase<TElement>> Logger;
    protected readonly string ManagerName;
    protected readonly ConcurrentDictionary<string, TElement> ElementInstances = new();

    protected Func<IElementBlueprint, IFireLogger, TElement> ElementFactory;
    private readonly ConcurrentDictionary<string, (string State, ElementHealth Health)> _lastElementStatus = new();
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);
    private CancellationToken _stoppingToken;

    public string? TestCounterpart => GetType().GetCustomAttribute<TestCounterpartAttribute>()?.CounterpartType.Name;

    /// abstract methods
    protected virtual void RegisterElementDestBonds(IElement element)
    {
  
        var devLogger = element.GetLogger();
        var bonds = ConfigurationLoader.GetAllReactionConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Destination.StartsWith(element.Config.Name));

        var ReactionBonds = bonds as BondBlueprint[] ?? bonds.ToArray();
        if (ReactionBonds.Length == 0) devLogger.Information("[{dec}] No Bonds found with a destination for this Element", element.Config.Name);
        foreach (var bond in ReactionBonds)
        {
            devLogger.Information("{method} [{Dev}]  Manager initializing Bond: {bond}","RegisterElementDestBonds", element.Config.Name, bond.Name);
            MessageBus.SubscribeAsync(bond.Destination, HandleBusMessageAsync);
        }

        // Always subscribe to the element name itself as a fallback
        MessageBus.SubscribeAsync(element.Config.Name, HandleBusMessageAsync);
    }

    protected virtual void OnElementCreated(IElement element)
    {
    }

    protected virtual void RegisterControlBonds(IElement element)
    {
        var controlTopic = $"SYS.CONTROL.{element.Config.Name.ToUpper()}";
        MessageBus.SubscribeAsync(controlTopic, async (envelope, ct) =>
        {
            try
            {
                var payload = envelope.Payload.ToString() ?? "";
                if (payload.Contains("RESTART", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote RESTART signal received.", element.Config.Name);
                    await ReinitializeElementAsync(element.Config.Name);
                }
                else if (payload.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote OFFLINE signal received.", element.Config.Name);
                    await TakeElementOfflineAsync(element.Config.Name);
                }
                else if (payload.Contains("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote ONLINE signal received.", element.Config.Name);
                    await ReinitializeElementAsync(element.Config.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error processing remote control command", element.Config.Name);
            }
        });
    }

    protected virtual Task OnElementMessageToMessageBusAsync(object? sender, object messageEnv) {  return Task.CompletedTask; }

    protected virtual Task RegisterElementSourceBonds(IElement element){ return Task.CompletedTask;}
    
    
    protected virtual Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct){ return Task.CompletedTask;}

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="elementFactory"></param>
    protected ElementManagerBase(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<TElement>> logger,
        Func<IElementBlueprint, IFireLogger, TElement> elementFactory,
        string managerName)
    {
        MessageBus = bus;
        Logger = logger;
        ElementConfigList = configs ?? new List<IElementBlueprint>();
        ElementFactory = elementFactory;
        ManagerName = managerName;
    }

    /// <summary>
    /// Starts all elements.
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stoppingToken = stoppingToken;
        Logger.Information("Starting Manager for {ElementType}", typeof(TElement).Name);

        // Initial load
        await ReconcileElementsAsync();

        // Subscribe to configuration changes
        ConfigurationLoader.OnConfigurationChanged += async () =>
        {
            Logger.Information("[{Manager}] Configuration change detected. Reconciling elements...", typeof(TElement).Name);
            await ReconcileElementsAsync();
        };

        // Subscribe to Global System Control for Status Refresh
        await MessageBus.SubscribeAsync(MessageBusTopic.SystemControl.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is Messaging.SystemControlMessage sysMsg && sysMsg.Command == Messaging.SystemCommand.RefreshStatus)
            {
                foreach (var element in ElementInstances.Values)
                {
                    element.RefreshStatus();
                }
            }
        });

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Centralized handler for all element status changes within this manager.
    /// </summary>
    private void OnElementStatusUpdated(IElement? sender, IElementStatus status)
    {
        if (sender is not { } element) return;

        string name = element.Config.Name;
        _lastElementStatus.TryGetValue(name, out var oldStatus);
        
        bool changed = oldStatus == default || oldStatus.State != status.State || oldStatus.Health != status.Health;

        if (changed)
        {
            _lastElementStatus[name] = (status.State, status.Health);
            Logger.Information("[{Dev}] Status Change: {State} (Health: {Health})",
                name, status.State, status.Health);
        }
        else
        {
            Logger.Verbose("[{Dev}] Status Update (Metrics/HB): {State} (Health: {Health})",
                name, status.State, status.Health);
        }

        _ = MessageBus.PublishAsync(MessageBusTopic.ElementStatus.ToString(),
            new MessageEnvelope(MessageBusTopic.ElementStatus, status));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="element"></param>
    protected virtual async Task AnnouncePresenceAsync(IElement element)
    {
        var announcement = new ElementAnnouncement
        {
            ElementName = element.Key.ElementName,
            ElementType = element.GetType().Name,
            SoftwareVersion = element.GetElementVersion(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            SchemaVersion = 1,
            AvailableCommands = element.GetAvailableCommands().ToList()
        };

        // Publish to a dedicated Discovery Topic
        await MessageBus.PublishAsync(MessageBusTopic.Discovery.ToString(),
            new MessageEnvelope(MessageBusTopic.Discovery, announcement));

        element.GetLogger().Information("[{Dev}] Presence announced to Diag Server.", element.Key.ElementName);
    }

    /// <summary>
    /// Serves as a factory method for creating element instances.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    protected virtual Task<TElement> CreateElementAsync(IElementBlueprint config)
    {
        var elementLogger = Logger.WithContext("ElementName", config.Name);
        var element = ElementFactory(config, elementLogger);
        return Task.FromResult(element);
    }

    private IElement? GetElementByName(string elementName)
    {
        foreach (var element in ElementInstances.Values.ToList())
        {
            if(element.Key.ElementName ==  elementName)
                return element;    
        }

        return null;
    }
    
    public async Task<bool> TakeElementOfflineAsync(string elementName)
    {
        var element = GetElementByName(elementName);
        if (element == null) return false;

        // 2. Stop the element (Take it down)
        // This typically involves cancelling its internal CancellationToken
        await element.StopAsync(CancellationToken.None);
        return true;

    }
    
    /// <summary>
    /// Returns the element instance with the specified name.
    /// </summary>
    /// <param name="elementName"></param>
    public async Task ReinitializeElementAsync(string elementName)
    {
        var element = GetElementByName(elementName);
        if (element == null) return ;
        
        // 3. Re-start/Initialize
        // In many WCS implementations, this involves re-running the StartAsync
        // which re-establishes TCP listeners/connections
        await element.StartAsync(CancellationToken.None);

        Logger.Information("[{Dev}] Element has been reinitialized.", elementName);
    }

    /// <summary>
    /// Registers all reaction bonds where the destination of the bond is equal to the element name.
    /// </summary>
    /// <param name="element"></param>
    protected virtual void PrepareForBondDestinations(IElement element)
    {
    }


    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Information("Stopping Manager for {ElementType}", typeof(TElement).Name);
        await StopElementsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    public async Task StopElementsAsync(CancellationToken cancellationToken)
    {
        foreach (var element in ElementInstances.Values.ToList())
        {
            await element.StopAsync(cancellationToken);
        }
    }

    private async Task ReconcileElementsAsync()
    {
        await _reconciliationLock.WaitAsync();
        try
        {
            var newConfigs = ConfigurationLoader.GetElementConfig(ManagerName);
            var activeConfigNames = newConfigs.Where(c => c.Enable).Select(c => c.Name).ToHashSet();

            // 1. Identify and Stop Removed or Disabled Elements
            var elementsToRemove = ElementInstances.Keys.Where(name => !activeConfigNames.Contains(name)).ToList();
            foreach (var name in elementsToRemove)
            {
                if (ElementInstances.TryRemove(name, out var element))
                {
                    Logger.Warning("[{Dev}] Configuration removed or disabled. Stopping element...", name);
                    await StopAndUnwireElementAsync(element);
                }
            }

            // 2. Identify Additions and Updates
            foreach (var config in newConfigs.Where(c => c.Enable))
            {
                if (ElementInstances.TryGetValue(config.Name, out var existingElement))
                {
                    // Check if properties have changed
                    if (ConfigHasChanged(existingElement.Config, config))
                    {
                        Logger.Information("[{Dev}] Configuration updated. Restarting element...", config.Name);
                        await StopAndUnwireElementAsync(existingElement);
                        await StartAndWireElementAsync(config);
                    }
                }
                else
                {
                    // New Element
                    Logger.Information("[{Dev}] New configuration detected. Starting element...", config.Name);
                    await StartAndWireElementAsync(config);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during element reconciliation");
        }
        finally
        {
            _reconciliationLock.Release();
        }
    }

    private async Task StartAndWireElementAsync(IElementBlueprint config)
    {
        try
        {
            var element = ElementFactory(config, Logger);
            ElementInstances[element.Key.ElementName] = element;

            if (element is IMessageProvider provider)
            {
                provider.MessageReceived += OnElementMessageToMessageBusAsync;
                Logger.LogDebug("[{Dev}] Messaging interface auto-wired.", config.Name);
            }
            element.StatusUpdated += OnElementStatusUpdated;

            PrepareForBondDestinations(element);
            RegisterElementDestBonds(element);
            RegisterControlBonds(element);

            if (element is IDiagnosticProvider diagProvider)
                await AnnouncePresenceAsync((IElement)diagProvider);

            _ = Task.Run(() => element.StartAsync(_stoppingToken), _stoppingToken);
            await RegisterElementSourceBonds(element);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start element {CustomerName}", config.Name);
        }
    }

    private async Task StopAndUnwireElementAsync(TElement element)
    {
        try
        {
            await element.StopAsync(CancellationToken.None);

            if (element is IMessageProvider provider)
            {
                provider.MessageReceived -= OnElementMessageToMessageBusAsync;
            }
            element.StatusUpdated -= OnElementStatusUpdated;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error during element shutdown", element.Config.Name);
        }
    }

    private bool ConfigHasChanged(IElementBlueprint oldConfig, IElementBlueprint newConfig)
    {
        if (oldConfig.Properties.Count != newConfig.Properties.Count) return true;
        foreach (var key in oldConfig.Properties.Keys)
        {
            if (!newConfig.Properties.TryGetValue(key, out var newValue) || 
                !Equals(oldConfig.Properties[key]?.ToString(), newValue?.ToString()))
            {
                return true;
            }
        }
        return false;
    }
}