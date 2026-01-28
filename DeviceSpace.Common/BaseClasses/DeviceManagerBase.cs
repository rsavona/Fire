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
    protected readonly ILogger<DeviceManagerBase<TDevice>> Logger;
    protected readonly List<TDevice> DeviceInstances = new();

    protected Func<IDeviceConfig, Serilog.ILogger, TDevice> DeviceFactory;

    /// abstract methods
    protected virtual void RegisterDeviceDesteRoutes(IDevice device)
    {

        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Destination.StartsWith(device.Config.Name));

        Logger.LogDebug("[{Dev}]  Manager initializing Routes: {Routes}", device.Config.Name, routes.Count());
        foreach (var route in routes)
        {
            MessageBus.SubscribeAsync(route.Destination, HandleBusMessageAsync);

        }
    }

    protected virtual void OnDeviceMessageReceivedAsync(object? sender, object messageEnv) { }

    protected virtual Task RegisterDeviceSourceRoutes(IDevice device){ return Task.CompletedTask;}
    
    
    protected virtual Task HandleBusMessageAsync(IDevice device, string routeSource, string routeDest,
        MessageEnvelope envelope, CancellationToken ct){ return Task.CompletedTask;}

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    protected DeviceManagerBase(IMessageBus bus, List<IDeviceConfig> configs,
        ILogger<DeviceManagerBase<TDevice>> logger,
        Func<IDeviceConfig, Serilog.ILogger, TDevice> deviceFactory)
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
        Logger.LogInformation("Starting Manager for {DeviceType}", typeof(TDevice).Name);

        // for each device in the config that this mmanager is responsible for
        foreach (var config in DeviceConfigList.Where(c => c.Enable))
        {
            Logger.LogTrace("Instantiating a device using DeviceFactory");
            var device = DeviceFactory(config, Log.Logger);
            Logger.LogTrace($"Device Created {device.Key.DeviceName}");
            DeviceInstances.Add(device);

            try
            {
                Logger.LogTrace(
                    "Adding the Device Managers 'OnDeviceMessageReceivedAsync' so that all messages are funneled through the manager. ");
                if (device is IMessageProvider provider)
                {
                    provider.MessageReceived += OnDeviceMessageReceivedAsync;
                    Logger.LogDebug("[{Dev}] Messaging interface auto-wired.", config.Name);
                }

                // staus update handler
                device.StatusUpdated += OnDeviceStatusUpdated;

                // announce presence to the Diag Server
                if (device is IDiagnosticProvider diagProvider)
                    await AnnouncePresenceAsync((IDevice)diagProvider);
                await device.StartAsync(stoppingToken);

                PrepareForRouteDestinations(device);
              //  RegisterDeviceDestRoutes(device);
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
        if (sender is not IDevice device) return;


        Logger.LogInformation("[{Dev}] Status Change: {State} (Health: {Health})",
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

        Logger.LogInformation("[{Dev}] Presence announced to Diag Server.", device.Key.DeviceName);
    }

    /// <summary>
    /// Serves as a factory method for creating device instances.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    protected virtual Task<TDevice> CreateDeviceAsync(IDeviceConfig config)
    {
        var deviceLogger = Log.ForContext("DeviceName", config.Name);
        var device = DeviceFactory(config, deviceLogger);
        return Task.FromResult(device);
    }

    private IDevice? GetDeviceByName(string deviceName)
    {
        foreach (var device in DeviceInstances)
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

        Logger.LogInformation("[{Dev}] Device has been reinitialized.", deviceName);
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
        foreach (var device in DeviceInstances)
        {
            await device.StopAsync(cancellationToken);
        }
    }
}