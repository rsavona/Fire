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
    protected override async Task  RegisterDeviceSourceRoutes(IDevice device)
    {
        // Get ALl the routes where the Source starts with the device name.  For every route found 
        // we call Read Notify for the queue on the external brkoer
        var routes = ConfigurationLoader.GetAllWorkflowConfig()
            .SelectMany(w => w.Routes)
            .Where(r => r.Source.StartsWith(device.Config.Name));
        
        Logger.LogDebug("[{Dev}]  Manager initializing Routes: {Routes}", device.Config.Name, routes.Count());
        foreach (var route in routes)
        {
            var queueName = new MessageBusTopic(route.Source).Discriminator;
            if (device is ActiveMqDevice dev)
            {
                var result = await dev.ReadNotifyAsync( queueName );
                if (result)
                {
                    Logger.LogDebug("[{Dev}] ActiveMQ Manager initialized Route: {Route}", device.Config.Name, route);
                    bool exists = _queueToBusMap.ContainsKey(queueName);
                    if (exists)
                    {
                        var list = _queueToBusMap[queueName];
                        list.Add(route.Source);
                    }
                    else
                    {
                        var list = new List<string> { route.Source };
                        _queueToBusMap[queueName] = list;
                    }
                }
                else
                {
                    var errorMsg = $"[{device.Config.Name}] Could not read route: {route}";
                    Logger.LogError(errorMsg);
                    device.OnError(errorMsg);
                }
            }
        }
    }
    
    /// <summary>
    /// Handle incoming messages from the external MQ. The ActiveMQ device and managers job is to get these
    /// messages and forward them to the internal message bus topic
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="message"></param>
    protected override void OnDeviceMessageReceivedAsync(object? sender, object message)
    {
        if (sender is SourceIdentifier devQue && message is string msg)
        {
            var sourceList = _queueToBusMap[devQue.SourcePath];
            foreach(var path in sourceList)
            {
                 var topic = new MessageBusTopic(path);
              
                 
                 Logger.LogDebug("[{Dev}] ActiveMQ Manager forwarding message to {topic}: {Msg}", devQue.DeviceKey, topic, msg);
                MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, msg));
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
    
