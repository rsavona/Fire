using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;
using CancellationToken = System.Threading.CancellationToken;
using Task = System.Threading.Tasks.Task;

namespace Device.Connector.ActiveMQ;

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
    public ActiveMqManager(IMessageBus bus, List<IDeviceConfig> config, ILogger<DeviceManagerBase<ActiveMqDevice>> logger,
        Func<IDeviceConfig, Serilog.ILogger, ActiveMqDevice> deviceFactory) : base(bus, config, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Register device handlers.
    /// </summary>
    /// <param name="device"></param>
    protected override void RegisterDeviceHandlers(ActiveMqDevice device)
    {
        // Pre-calculate routes from config
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Source.StartsWith(device.Config.Name));
        Logger.LogDebug("[{Dev}] ActiveMQ Manager initializing Routes: {Routes}", device.Config.Name, routes.Count());
        foreach (var route in routes)
        {
            var queueName = new MessageBusTopic(route.Destination).Discriminator;
            _queueToBusMap.AddOrUpdate(queueName, new List<string> { route.Destination }, (k, l) =>
            {
                l.Add(route.Destination);
                return l;
            });
            device.ReadNotify(OnDeviceMessageReceivedAsync, queueName);
            Logger.LogDebug("[{Dev}] ActiveMQ Manager initialized Route: {Route}", device.Config.Name, route);
        }
    }


    /// <summary>
    /// Handle incoming messages from the external MQ. The ActiveMQ device and managers job is to get these
    /// messages and forward them to the internal message bus topic
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="env"></param>
    protected override void OnDeviceMessageReceivedAsync(object? sender, object env)
    {
        if (sender is not ActiveMqDevice dev || env is not MessageEnvelope msg) return;
        Logger.LogDebug("[{Dev}] ActiveMQ Manager received message from {Src}: {Msg}", dev.Config.Name, msg.Destination, msg.Payload);
        string queue = msg.Destination.ToString();
        if (_queueToBusMap.TryGetValue(queue, out var targets))
        {
            foreach (var t in targets)
                MessageBus.PublishAsync(t, new MessageEnvelope(new MessageBusTopic(t), msg.Payload));
        }
    }

    /// <summary>
    ///  Handle incoming messages from the internal message bus that get forwarded to the ActiveMQ.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="src"></param>
    /// <param name="env"></param>
    /// <param name="ct"></param>
    protected override async Task HandleBusMessageAsync(ActiveMqDevice device, string src, string dest, MessageEnvelope env,
        CancellationToken ct)
    {
        var topic = new MessageBusTopic(dest);
        Logger.LogDebug("[{Dev}] ActiveMQ Manager received message from {Src}: {Msg}", device.Config.Name, src, env.Payload);
        await device.WriteAsync(env.Payload.ToString(), topic.Discriminator);
    }
}