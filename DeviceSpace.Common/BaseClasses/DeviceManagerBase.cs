using System.Collections.Concurrent;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DeviceSpace.Common.BaseClasses;

/// <summary>
/// Base class for Device Managers.
/// </summary>
/// <typeparam name="TDevice"></typeparam>
public abstract class DeviceManagerBase<TDevice> : BackgroundService, IDeviceManager
    where TDevice : IDevice
{
    protected readonly IMessageBus MessageBus;
    protected readonly List<IDeviceConfig> DeviceConfigList;
    protected readonly IFireLogger<DeviceManagerBase<TDevice>> Logger;
    protected readonly string ManagerName;
    protected readonly ConcurrentDictionary<string, TDevice> DeviceInstances = new();

    protected Func<IDeviceConfig, IFireLogger, TDevice> DeviceFactory;
    private readonly ConcurrentDictionary<string, (string State, DeviceHealth Health)> _lastDeviceStatus = new();
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);
    private CancellationToken _stoppingToken;

    /// abstract methods
    protected virtual void RegisterDeviceDestRoutes(IDevice device)
    {
  
        var devLogger = device.GetLogger();
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Destination.StartsWith(device.Config.Name));

        var workflowRoutes = routes as WorkflowRoute[] ?? routes.ToArray();
        if (workflowRoutes.Length == 0) devLogger.Information("[{dec}] No Routes found with a destination for this Device", device.Config.Name);
        foreach (var route in workflowRoutes)
        {
            devLogger.Information("{method} [{Dev}]  Manager initializing Route: {route}","RegisterDeviceDestRoutes", device.Config.Name, route.Name);
            MessageBus.SubscribeAsync(route.Destination, HandleBusMessageAsync);
        }

        // Always subscribe to the device name itself as a fallback
        MessageBus.SubscribeAsync(device.Config.Name, HandleBusMessageAsync);
    }

    protected virtual void OnDeviceCreated(IDevice device)
    {
    }

    protected virtual void RegisterControlRoutes(IDevice device)
    {
        var controlTopic = $"SYS.CONTROL.{device.Config.Name.ToUpper()}";
        MessageBus.SubscribeAsync(controlTopic, async (envelope, ct) =>
        {
            try
            {
                var payload = envelope.Payload.ToString() ?? "";
                if (payload.Contains("RESTART", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote RESTART signal received.", device.Config.Name);
                    await ReinitializeDeviceAsync(device.Config.Name);
                }
                else if (payload.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote OFFLINE signal received.", device.Config.Name);
                    await TakeDeviceOfflineAsync(device.Config.Name);
                }
                else if (payload.Contains("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("[{Dev}] Remote ONLINE signal received.", device.Config.Name);
                    await ReinitializeDeviceAsync(device.Config.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Dev}] Error processing remote control command", device.Config.Name);
            }
        });
    }

    protected virtual Task OnDeviceMessageToMessageBusAsync(object? sender, object messageEnv) {  return Task.CompletedTask; }

    protected virtual Task RegisterDeviceSourceRoutes(IDevice device){ return Task.CompletedTask;}
    
    
    protected virtual Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct){ return Task.CompletedTask;}

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    protected DeviceManagerBase(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<TDevice>> logger,
        Func<IDeviceConfig, IFireLogger, TDevice> deviceFactory,
        string managerName)
    {
        MessageBus = bus;
        Logger = logger;
        DeviceConfigList = configs ?? new List<IDeviceConfig>();
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
    /// Centralized handler for all device status changes within this manager.
    /// </summary>
    private void OnDeviceStatusUpdated(IDevice? sender, IDeviceStatus status)
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
    /// <param name="device"></param>
    protected virtual async Task AnnouncePresenceAsync(IDevice device)
    {
        var announcement = new DeviceAnnouncement
        {
            DeviceName = device.Key.DeviceName,
            DeviceType = device.GetType().Name,
            SoftwareVersion = device.GetDeviceVersion(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            SchemaVersion = 1,
            AvailableCommands = device.GetAvailableCommands().ToList()
        };

        // Publish to a dedicated Discovery Topic
        await MessageBus.PublishAsync(MessageBusTopic.Discovery.ToString(),
            new MessageEnvelope(MessageBusTopic.Discovery, announcement));

        device.GetLogger().Information("[{Dev}] Presence announced to Diag Server.", device.Key.DeviceName);
    }

    /// <summary>
    /// Serves as a factory method for creating device instances.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    protected virtual Task<TDevice> CreateDeviceAsync(IDeviceConfig config)
    {
        var deviceLogger = Logger.WithContext("DeviceName", config.Name);
        var device = DeviceFactory(config, deviceLogger);
        return Task.FromResult(device);
    }

    private IDevice? GetDeviceByName(string deviceName)
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

        // 2. Stop the device (Take it down)
        // This typically involves cancelling its internal CancellationToken
        await device.StopAsync(CancellationToken.None);
        return true;

    }
    
    /// <summary>
    /// Returns the device instance with the specified name.
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
    /// Registers all workflow routes where the destination of the route is equal to the device name.
    /// </summary>
    /// <param name="device"></param>
    protected virtual void PrepareForRouteDestinations(IDevice device)
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
                    Logger.Warning("[{Dev}] Configuration removed or disabled. Stopping device...", name);
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
                        Logger.Information("[{Dev}] Configuration updated. Restarting device...", config.Name);
                        await StopAndUnwireDeviceAsync(existingDevice);
                        await StartAndWireDeviceAsync(config);
                    }
                }
                else
                {
                    // New Device
                    Logger.Information("[{Dev}] New configuration detected. Starting device...", config.Name);
                    await StartAndWireDeviceAsync(config);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during device reconciliation");
        }
        finally
        {
            _reconciliationLock.Release();
        }
    }

    private async Task StartAndWireDeviceAsync(IDeviceConfig config)
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
            RegisterDeviceDestRoutes(device);
            RegisterControlRoutes(device);

            if (device is IDiagnosticProvider diagProvider)
                await AnnouncePresenceAsync((IDevice)diagProvider);

            _ = Task.Run(() => device.StartAsync(_stoppingToken), _stoppingToken);
            await RegisterDeviceSourceRoutes(device);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start device {Name}", config.Name);
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
            Logger.Error(ex, "[{Dev}] Error during device shutdown", device.Config.Name);
        }
    }

    private bool ConfigHasChanged(IDeviceConfig oldConfig, IDeviceConfig newConfig)
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