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

    protected readonly Func<IDeviceConfig, Serilog.ILogger, TDevice> _deviceFactory;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="configs"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    protected DeviceManagerBase(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<TDevice>> logger, 
            Func<IDeviceConfig, Serilog.ILogger, TDevice> deviceFactory)
    {
        MessageBus = bus;
        Logger = logger;
        DeviceConfigList = configs ?? new List<IDeviceConfig>();
        _deviceFactory = deviceFactory;
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
            var device = _deviceFactory(config, Serilog.Log.Logger);
            DeviceInstances.Add(device);
            try
            {
                // message handler for all external communication (not from the message bus) coming into the device 
                if (device is IMessageProvider provider)
                {
                    provider.MessageReceived += OnDeviceMessageReceivedAsync;
                    Logger.LogDebug("[{Dev}] Messaging interface auto-wired.", config.Name);
                }
                // staus update handler
                device.StatusUpdated += OnDeviceStatusUpdated;
                // handlers for device specific functionality
                RegisterDeviceHandlers(device);
                // register all workflow routes
                RegisterWorkflowSubscriptions(device);
                // announce presence to the Diag Server
                if (device is IDiagnosticProvider diagProvider)
                    await AnnouncePresenceAsync((IDevice)diagProvider);
                await device.StartAsync(stoppingToken);
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
       var device = _deviceFactory(config, deviceLogger);
       return Task.FromResult(device);
    }

    /// abstract methods
    protected abstract void RegisterDeviceHandlers(TDevice device);

    protected abstract void OnDeviceMessageReceivedAsync(object? sender, object messageEnv);

    protected abstract Task HandleBusMessageAsync(TDevice device, string routeSource, string routeDest,
        MessageEnvelope envelope,
        CancellationToken ct);

    /// <summary>
    /// Register all Workflow Route Destinations
    /// where the topic starts with the Device Name.
    /// </summary>
    /// <param name="device"></param>
    private void RegisterWorkflowSubscriptions(TDevice device)
    {
        var workflows = ConfigurationLoader.GetAllWorkflowConfig();
        foreach (var workflow in workflows)
        {
            foreach (var route in workflow.Routes)
            {
                if (route.Destination.StartsWith(device.Config.Name))
                {
                    Logger.LogDebug("[{Dev}] Subscribing to {Route}", device.Config.Name, route.Destination);
                    MessageBus.SubscribeAsync(route.Destination,
                        (msg, ct) =>
                            HandleBusMessageAsync(device, route.Source, route.Destination.ToString(), msg, ct));
                }
            }
        }
    }
    
    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var device in DeviceInstances)
        {
            await device.StopAsync(cancellationToken);
        }
    }
}