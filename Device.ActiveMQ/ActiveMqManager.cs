using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Logging;
using Microsoft.Extensions.Logging;
using CancellationToken = System.Threading.CancellationToken;
using Task = System.Threading.Tasks.Task;


namespace Device.ActiveMQ;

public class ActiveMqManager : DeviceManagerBase<ActiveMqDevice>
{
    private readonly ConcurrentDictionary<string, List<string>> _queueToBusMap = new();

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    /// <param name="deviceFactory"></param>
    public ActiveMqManager(
        IMessageBus bus,
        List<IDeviceConfig> config,
        IFireLogger<ActiveMqManager> logger, // Change to the specific manager type
        Func<IDeviceConfig, IFireLogger, ActiveMqDevice> deviceFactory)
        : base(bus, config, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Registers the destination routes for the specified device if it is of type ActiveMqDevice.
    /// </summary>
    /// <param name="device">The device for which destination routes will be registered, expected to be an ActiveMqDevice.</param>
    protected override void RegisterDeviceDestRoutes(IDevice device)
    {
        var deviceLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);
        if (device is not ActiveMqDevice mqdev)
        {
            deviceLogger.Error("Wrong Device type");
            return;
        }

        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Destination.StartsWith(device.Config.Name)).ToList();
        
        deviceLogger.Information("[{device}] Manager initializing destination {count} Routes", device.Key.DeviceName,
            routes.Count());
        foreach (var route in routes)
        {
            mqdev.GetLogger().Information($"[{device.Config.Name}] Manager initializing Route: {route.Name}");
            MessageBus.SubscribeAsync(route.Destination, HandleBusMessageAsync);
        }
    }

    /// <summary>
    /// Finds routes whose source starts with the device name and registers them with the ActiveMQ device.
    /// </summary>
    /// <param name="device"></param>
    protected override async Task RegisterDeviceSourceRoutes(IDevice device)
    {
        var deviceLogger = Logger.WithContext("DeviceName", device.Key.DeviceName);
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Source.StartsWith(device.Config.Name) && r.Mode > 0)
            .ToList();

        deviceLogger.Information("[{device}] Manager initializing Source {count} Routes", device.Key.DeviceName,
            routes.Count());

        foreach (var route in routes)
        {
            deviceLogger.Information($"[{device.Config.Name}] Manager initializing Routes: {route.Name}");
            var queueName = new MessageBusTopic(route.Source).Discriminator;
            if (device is not ActiveMqDevice dev) {
                deviceLogger.Error("Wrong Device type");
                continue;
            }   
            var result = await dev.ReadNotifyAsync(queueName); // Will notify the device when messages come in
            if (result)
            {
                Logger.LogDebug($"[{device.Config.Name}] ActiveMQ Manager initialized Route: {route}");

                if (_queueToBusMap.TryGetValue(queueName, out var list))
                {
                    list.Add(route.Source);
                }
                else
                {
                    _queueToBusMap[queueName] = new List<string> { route.Source };
                }
            }
            else
            {
                var errorMsg = $"[{device.Config.Name}] Could not read route: {route}";
                Logger.LogError(null, errorMsg);
                device.OnError(errorMsg);
            }
        }
    }

    /// <summary>
    /// Handle incoming messages from the external MQ. The ActiveMQ device and managers job is to get these
    /// messages and forward them to the internal message bus topic
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="message"></param>
    protected override Task OnDeviceMessageToMessageBusAsync(object? message, object sender)
    {
        if (sender is not string devQue || message is not string msg) return Task.CompletedTask;

        var topicNameList = _queueToBusMap[devQue];
        foreach (var path in topicNameList)
        {
            var topic = new MessageBusTopic(path);

            Logger.LogDebug("[{Dev}] ActiveMQ Manager forwarding message to {topic}: {Msg}", devQue,
                topic, msg);
            MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, msg));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///  Handle incoming messages from the internal message bus that get forwarded to the ActiveMQ.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope env, CancellationToken ct)
    {
        // Using Task.Run is fine, but ensure we don't block the caller
        _ = Task.Run(async () =>
        {
            try
            {
                var deviceName = env.Destination.DeviceName;
                var queue = env.Destination.Discriminator;
                var device = DeviceInstances[deviceName];
                var deviceLogger = Logger.WithContext("DeviceName", deviceName);
                deviceLogger.LogDebug($"[{device.Config.Name}] ActiveMQ Manager received {env.Payload}");
                await device.WriteAsync(env.Payload.ToJson(), queue);
                
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Background task failed in ActiveMqManager");
            }
        }, ct);

        await Task.CompletedTask;
    }
}