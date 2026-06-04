using System.Collections.Concurrent;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Fusion.Common.BaseClasses;

/// <summary>
/// Base class for Device Managers.
/// </summary>
/// <typeparam name="TDevice"></typeparam>
public abstract class DeviceManagerBase<TDevice> : BackgroundService, IDeviceManager
    where TDevice : IElement
{
    protected readonly IMessageBus MessageBus;
    protected readonly List<IElementBlueprint> DeviceConfigList;
    protected readonly IFireLogger<DeviceManagerBase<TDevice>> Logger;
    protected readonly string ManagerName;
    protected readonly ConcurrentDictionary<string, TDevice> DeviceInstances = new();

    protected Func<IElementBlueprint, IFireLogger, TDevice> DeviceFactory;
    private readonly ConcurrentDictionary<string, (string State, DeviceHealth Health)> _lastDeviceStatus = new();
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);
    private CancellationToken _stoppingToken;

    /// abstract methods
    protected virtual void RegisterDeviceDestBonds(IElement element)
    {
  
        var devLogger = element.GetLogger();
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Destination.StartsWith(element.Config.Name));

        var workflowBonds = routes as Bonds[] ?? routes.ToArray();
        if (workflowBonds.Length == 0) devLogger.Information("[{dec}] No Bonds found with a destination for this Device", element.Config.Name);
        foreach (var route in workflowBonds)
        {
            devLogger.Information("{method} [{Dev}]  Manager initializing Bond: {route}","RegisterDeviceDestBonds", element.Config.Name, route.Name);
            MessageBus.SubscribeAsync(route.Destination, HandleBusMessageAsync);
        }

        // Always subscribe to the element name itself as a fallback
        MessageBus.SubscribeAsync(element.Config.Name, HandleBusMessageAsync);
    }

    protected virtual void OnDeviceCreated(IElement element)
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
                    await ReinitializeDeviceAsync(element.Config.Name);
                }
                else if (payload.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote OFFLINE signal received.", element.Config.Name);
                    await TakeDeviceOfflineAsync(element.Config.Name);
                }
                else if (payload.Contains("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote ONLINE signal received.", element.Config.Name);
                    await ReinitializeDeviceAsync(element.Config.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error processing remote control command", element.Config.Name);
            }
        });
    }

    protected virtual Task OnDeviceMessageToMessageBusAsync(object? sender, object messageEnv) {  return Task.CompletedTask; }

    protected virtual Task RegisterDeviceSourceBonds(IElement element){ return Task.CompletedTask;}
    
    
    protected virtual Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct){ return Task.CompletedTask;}

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    protected DeviceManagerBase(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<DeviceManagerBase<TDevice>> logger,
        Func<IElementBlueprint, IFireLogger, TDevice> deviceFactory,
        string managerName)
    {
        MessageBus = bus;
        Logger = logger;
        DeviceConfigList = configs ?? new List<IElementBlueprint>();
        DeviceFactory = deviceFactory;
        ManagerName = managerName;
    }

    /// <summary>
    /// Starts all devices.
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stoppingToken = stoppingToken;
        Logger.Information("Starting Manager for {DeviceType}", typeof(TDevice).Name);

        // Initial load
        await ReconcileDevicesAsync();

        // Subscribe to configuration changes
        ConfigurationLoader.OnConfigurationChanged += async () =>
        {
            Logger.Information("[{Manager}] Configuration change detected. Reconciling devices...", typeof(TDevice).Name);
            await ReconcileDevicesAsync();
        };

        // Subscribe to Global System Control for Status Refresh
        await MessageBus.SubscribeAsync(MessageBusTopic.SystemControl.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is Messaging.SystemControlMessage sysMsg && sysMsg.Command == Messaging.SystemCommand.RefreshStatus)
            {
                foreach (var device in DeviceInstances.Values)
                {
                    device.RefreshStatus();
                }
            }
        });

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Centralized handler for all element status changes within this manager.
    /// </summary>
    private void OnDeviceStatusUpdated(IElement? sender, IDeviceStatus status)
    {
        if (sender is not { } device) return;

        string name = device.Config.Name;
        _lastDeviceStatus.TryGetValue(name, out var oldStatus);
        
        bool changed = oldStatus == default || oldStatus.State != status.State || oldStatus.Health != status.Health;

        if (changed)
        {
            _lastDeviceStatus[name] = (status.State, status.Health);
            Logger.Information("[{Dev}] Status Change: {State} (Health: {Health})",
                name, status.State, status.Health);
        }
        else
        {
            Logger.Verbose("[{Dev}] Status Update (Metrics/HB): {State} (Health: {Health})",
                name, status.State, status.Health);
        }

        _ = MessageBus.PublishAsync(MessageBusTopic.DeviceStatus.ToString(),
            new MessageEnvelope(MessageBusTopic.DeviceStatus, status));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="element"></param>
    protected virtual async Task AnnouncePresenceAsync(IElement element)
    {
        var announcement = new DeviceAnnouncement
        {
            DeviceName = element.Key.DeviceName,
            DeviceType = element.GetType().Name,
            SoftwareVersion = element.GetDeviceVersion(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            SchemaVersion = 1,
            AvailableCommands = element.GetAvailableCommands().ToList()
        };

        // Publish to a dedicated Discovery Topic
        await MessageBus.PublishAsync(MessageBusTopic.Discovery.ToString(),
            new MessageEnvelope(MessageBusTopic.Discovery, announcement));

        element.GetLogger().Information("[{Dev}] Presence announced to Diag Server.", element.Key.DeviceName);
    }

    /// <summary>
    /// Serves as a factory method for creating element instances.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    protected virtual Task<TDevice> CreateDeviceAsync(IElementBlueprint config)
    {
        var deviceLogger = Logger.WithContext("DeviceName", config.Name);
        var device = DeviceFactory(config, deviceLogger);
        return Task.FromResult(device);
    }

    private IElement? GetDeviceByName(string deviceName)
    {
        foreach (var device in DeviceInstances.Values.ToList())
        {
            if(device.Key.DeviceName ==  deviceName)
                return device;    
        }

        return null;
    }
    
    public async Task<bool> TakeDeviceOfflineAsync(string deviceName)
    {
        var device = GetDeviceByName(deviceName);
        if (device == null) return false;

        // 2. Stop the element (Take it down)
        // This typically involves cancelling its internal CancellationToken
        await device.StopAsync(CancellationToken.None);
        return true;

    }
    
    /// <summary>
    /// Returns the element instance with the specified name.
    /// </summary>
    /// <param name="deviceName"></param>
    public async Task ReinitializeDeviceAsync(string deviceName)
    {
        var device = GetDeviceByName(deviceName);
        if (device == null) return ;
        
        // 3. Re-start/Initialize
        // In many WCS implementations, this involves re-running the StartAsync
        // which re-establishes TCP listeners/connections
        await device.StartAsync(CancellationToken.None);

        Logger.Information("[{Dev}] Device has been reinitialized.", deviceName);
    }

    /// <summary>
    /// Registers all workflow routes where the destination of the route is equal to the element name.
    /// </summary>
    /// <param name="element"></param>
    protected virtual void PrepareForRouteDestinations(IElement element)
    {
    }


    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Information("Stopping Manager for {DeviceType}", typeof(TDevice).Name);
        await StopDevicesAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    public async Task StopDevicesAsync(CancellationToken cancellationToken)
    {
        foreach (var device in DeviceInstances.Values.ToList())
        {
            await device.StopAsync(cancellationToken);
        }
    }

    private async Task ReconcileDevicesAsync()
    {
        await _reconciliationLock.WaitAsync();
        try
        {
            var newConfigs = ConfigurationLoader.GetDeviceConfig(ManagerName);
            var activeConfigNames = newConfigs.Where(c => c.Enable).Select(c => c.Name).ToHashSet();

            // 1. Identify and Stop Removed or Disabled Devices
            var devicesToRemove = DeviceInstances.Keys.Where(name => !activeConfigNames.Contains(name)).ToList();
            foreach (var name in devicesToRemove)
            {
                if (DeviceInstances.TryRemove(name, out var device))
                {
                    Logger.Warning("[{Dev}] Configuration removed or disabled. Stopping element...", name);
                    await StopAndUnwireDeviceAsync(device);
                }
            }

            // 2. Identify Additions and Updates
            foreach (var config in newConfigs.Where(c => c.Enable))
            {
                if (DeviceInstances.TryGetValue(config.Name, out var existingDevice))
                {
                    // Check if properties have changed
                    if (ConfigHasChanged(existingDevice.Config, config))
                    {
                        Logger.Information("[{Dev}] Configuration updated. Restarting element...", config.Name);
                        await StopAndUnwireDeviceAsync(existingDevice);
                        await StartAndWireDeviceAsync(config);
                    }
                }
                else
                {
                    // New Device
                    Logger.Information("[{Dev}] New configuration detected. Starting element...", config.Name);
                    await StartAndWireDeviceAsync(config);
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

    private async Task StartAndWireDeviceAsync(IElementBlueprint config)
    {
        try
        {
            var device = DeviceFactory(config, Logger);
            DeviceInstances[device.Key.DeviceName] = device;

            if (device is IMessageProvider provider)
            {
                provider.MessageReceived += OnDeviceMessageToMessageBusAsync;
                Logger.LogDebug("[{Dev}] Messaging interface auto-wired.", config.Name);
            }
            device.StatusUpdated += OnDeviceStatusUpdated;

            PrepareForRouteDestinations(device);
            RegisterDeviceDestBonds(device);
            RegisterControlBonds(device);

            if (device is IDiagnosticProvider diagProvider)
                await AnnouncePresenceAsync((IElement)diagProvider);

            _ = Task.Run(() => device.StartAsync(_stoppingToken), _stoppingToken);
            await RegisterDeviceSourceBonds(device);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start element {CustomerName}", config.Name);
        }
    }

    private async Task StopAndUnwireDeviceAsync(TDevice device)
    {
        try
        {
            await device.StopAsync(CancellationToken.None);

            if (device is IMessageProvider provider)
            {
                provider.MessageReceived -= OnDeviceMessageToMessageBusAsync;
            }
            device.StatusUpdated -= OnDeviceStatusUpdated;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error during element shutdown", device.Config.Name);
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