using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
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
    public ActiveMqManager(IMessageBus bus, List<IDeviceConfig> config, ILogger<DeviceManagerBase<ActiveMqDevice>> logger,
        Func<IDeviceConfig, Serilog.ILogger, ActiveMqDevice> deviceFactory) : base(bus, config, logger, deviceFactory)
    {
    }

    /// <summary>
    /// Finds routes whose source starts with the device name and registers them with the ActiveMQ device.
    /// </summary>
    /// <param name="device"></param>
    protected override void RegisterRouteSources(IDevice device)
    {
        // Pre-calculate routes from config
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Source.StartsWith(device.Config.Name));
        Logger.LogDebug("[{Dev}] ActiveMQ Manager initializing Routes: {Routes}", device.Config.Name, routes.Count());
        foreach (var route in routes)
        {
            var queueName = new MessageBusTopic(route.Source).Discriminator;
            _queueToBusMap.AddOrUpdate(queueName, new List<string> { route.Destination }, (k, l) =>
            {
                l.Add(route.Destination);
                return l;
            });
            if (device is ActiveMqDevice dev)
            {
                dev.ReadNotify(OnDeviceMessageReceivedAsync, queueName);
                Logger.LogDebug("[{Dev}] ActiveMQ Manager initialized Route: {Route}", device.Config.Name, route);
            }
        }
    }


    /// <summary>
    /// Handle incoming messages from the external MQ. The ActiveMQ device and managers job is to get these
    /// messages and forward them to the internal message bus topic
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="msg"></param>
    protected override void OnDeviceMessageReceivedAsync(object? sender, object msg)
    {
        if (sender is not ActiveMqDevice dev || msg is not MessageEnvelope env)  return;
      //  Logger.LogDebug("[{Dev}] ActiveMQ Manager received message from {Src}: {Msg}", dev.Config.Name, msg.Destination, msg.Payload);
      //  string queue = msg.Destination.ToString();
        if (_queueToBusMap.TryGetValue(env.Destination.Discriminator, out var targets))
        {
            foreach (var t in targets)
            {
                Logger.LogDebug( "[{Dev}] ActiveMQ Manager forwarding message to {Target}: {Msg}", dev.Config.Name, t, env.Payload);
                MessageBus.PublishAsync(t, new MessageEnvelope(new MessageBusTopic(t), msg));
            }
        }
    }

    /// <summary>
    ///  Handle incoming messages from the internal message bus that get forwarded to the ActiveMQ.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="src"></param>
    /// <param name="env"></param>
    /// <param name="ct"></param>
    protected override async Task HandleBusMessageAsync(IDevice device, string src, string dest, MessageEnvelope env,
        CancellationToken ct)
    {
        var topic = new MessageBusTopic(dest);
        Logger.LogDebug("[{Dev}] ActiveMQ Manager received message from {Src}: {Msg}", device.Config.Name, src, env.Payload);
        if (device is ActiveMqDevice dev)
        {
            await dev.WriteAsync(env.Payload.ToString(), topic.Discriminator);
        }
    }
}