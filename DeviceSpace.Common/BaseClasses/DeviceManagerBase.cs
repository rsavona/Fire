using System.Collections.Concurrent;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
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
    protected readonly ConcurrentDictionary<string, TDevice> DeviceInstances = new();

    protected Func<IDeviceConfig, IFireLogger, TDevice> DeviceFactory;

    /// abstract methods
    protected virtual void RegisterDeviceDestRoutes(IDevice device)
    {
   
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Destination.StartsWith(device.Config.Name));

       
        foreach (var route in routes)
        {
            device.GetLogger().Information("{method} [{Dev}]  Manager initializing Route: {route}","RegisterDeviceDestRoutes", device.Config.Name, route.Name);
            MessageBus.SubscribeAsync(route.Destination, HandleBusMessageAsync);
        }
    }

    protected virtual void OnDeviceCreated(IDevice device)
    {
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
        Func<IDeviceConfig, IFireLogger, TDevice> deviceFactory)
    {
        MessageBus = bus;
        Logger = logger;
        DeviceConfigList = configs ?? new List<IDeviceConfig>();
        DeviceFactory = deviceFactory;
    }

    /// <summary>
    /// Starts all devices.
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Information("Starting Manager for {DeviceType}", typeof(TDevice).Name);

        // for each device in the config that this mmanager is responsible for
        foreach (var config in DeviceConfigList.Where(c => c.Enable))
        {
            var device = DeviceFactory(config, Logger);
            Logger.Verbose($"Device Created {device.Key.DeviceName}");
            DeviceInstances[device.Key.DeviceName] = device;

            try
            {
                Logger.Verbose(
                    "Adding the Device Managers 'OnDeviceMessageToMessageBusAsync' so that all messages are funneled through the manager. ");
                if (device is IMessageProvider provider)
                {
                    provider.MessageReceived += OnDeviceMessageToMessageBusAsync;
                    Logger.LogDebug("[{Dev}] Messaging interface auto-wired.", config.Name);
                }
                // staus update handler
                device.StatusUpdated += OnDeviceStatusUpdated;
                
                PrepareForRouteDestinations(device);
                RegisterDeviceDestRoutes(device);
               
                // announce presence to the Diag Server
                if (device is IDiagnosticProvider diagProvider)
                    await AnnouncePresenceAsync((IDevice)diagProvider);
               
                _ = Task.Run(() => device.StartAsync(stoppingToken), stoppingToken);
                 await RegisterDeviceSourceRoutes(device);
            
            }
            catch (Exception ex)
            {
                device.OnError("Manager Exec Async Error ", ex);
                Logger.LogError(ex, "Failed to start device {Name}", config.Name);
            }
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Centralized handler for all device status changes within this manager.
    /// </summary>
    private void OnDeviceStatusUpdated(IDevice? sender, IDeviceStatus status)
    {
        if (sender is not { } device) return;


        Logger.Information("[{Dev}] Status Change: {State} (Health: {Health})",
            device.Config.Name, status.State, status.Health);

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
        foreach (var device in DeviceInstances.Values.ToList())
        {
            await device.StopAsync(cancellationToken);
        }
    }
}