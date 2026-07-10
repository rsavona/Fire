using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using CancellationToken = System.Threading.CancellationToken;
using Task = System.Threading.Tasks.Task;


namespace Fusion.Element.ActiveMQ;

public class ActiveMqManager : ElementManagerBase<ActiveMqElement>
{
    private readonly ConcurrentDictionary<string, List<string>> _queueToBusMap = new();

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    /// <param name="elementFactory"></param>
    public ActiveMqManager(
        IMessageBus bus,
        List<IElementBlueprint> config,
        IFireLogger<ActiveMqManager> logger, // Change to the specific manager type
        Func<IElementBlueprint, IFireLogger, ActiveMqElement> elementFactory,
        string managerName)
        : base(bus, config, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Registers the destination bonds for the specified element if it is of type ActiveMqElement.
    /// </summary>
    /// <param name="element">The element for which destination bonds will be registered, expected to be an ActiveMqElement.</param>
    protected override void RegisterElementDestBonds(IElement element)
    {
        var elementLogger = Logger.WithContext("ElementName", element.Key.ElementName);
        if (element is not ActiveMqElement mqdev)
        {
            elementLogger.Error("Wrong Element type");
            return;
        }

        var bonds = ConfigurationLoader.GetAllReactionConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Destination.StartsWith(element.Config.Name)).ToList();
        
        elementLogger.Information("[{element}] Manager initializing destination {count} Bonds", element.Key.ElementName,
            bonds.Count());
        foreach (var bond in bonds)
        {
            mqdev.GetLogger().Information($"[{element.Config.Name}] Manager initializing Bond: {bond.Name}");
            MessageBus.SubscribeAsync(bond.Destination, HandleBusMessageAsync);
        }
    }

    /// <summary>
    /// Finds bonds whose source starts with the element name and registers them with the ActiveMQ element.
    /// </summary>
    /// <param name="element"></param>
    protected override async Task RegisterElementSourceBonds(IElement element)
    {
        var elementLogger = Logger.WithContext("ElementName", element.Key.ElementName);
        var bonds = ConfigurationLoader.GetAllReactionConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Source.StartsWith(element.Config.Name) && r.Mode > 0)
            .ToList();

        elementLogger.Information("[{element}] Manager initializing Source {count} Bonds", element.Key.ElementName,
            bonds.Count());

        foreach (var bond in bonds)
        {
            elementLogger.Information($"[{element.Config.Name}] Manager initializing Bonds: {bond.Name}");
            var queueName = new MessageBusTopic(bond.Source).Discriminator;
            if (element is not ActiveMqElement dev) {
                elementLogger.Error("Wrong Element type");
                continue;
            }   
            var result = await dev.ReadNotifyAsync(queueName); // Will notify the element when messages come in
            if (result)
            {
                Logger.LogDebug($"[{element.Config.Name}] ActiveMQ Manager initialized Bond: {bond}");

                if (_queueToBusMap.TryGetValue(queueName, out var list))
                {
                    list.Add(bond.Source);
                }
                else
                {
                    _queueToBusMap[queueName] = new List<string> { bond.Source };
                }
            }
            else
            {
                var errorMsg = $"[{element.Config.Name}] Could not read bond: {bond}";
                Logger.LogError(null, errorMsg);
                element.OnError(errorMsg);
            }
        }
    }

    /// <summary>
    /// Handle incoming messages from the external MQ. The ActiveMQ element and managers job is to get these
    /// messages and forward them to the internal message bus topic
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="message"></param>
    protected override Task OnElementMessageToMessageBusAsync(object? message, object sender)
    {
        if (sender is not string devQue || message == null) return Task.CompletedTask;

        if (!_queueToBusMap.TryGetValue(devQue, out var topicNameList))
        {
            Logger.LogWarning("No mapping found for queue {Queue}", devQue);
            return Task.CompletedTask;
        }

        foreach (var path in topicNameList)
        {
            var topic = new MessageBusTopic(path);

            Logger.LogDebug("[{Dev}] ActiveMQ Manager forwarding message to {topic}", devQue, topic);
            MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, message));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///  Handle incoming messages from the internal message bus that get forwarded to the ActiveMQ.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope env, CancellationToken ct)
    {
        var elementName = env.Destination.ElementName;
        if (!ElementInstances.TryGetValue(elementName, out var element))
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", elementName);
            return;
        }

        // Using Task.Run is fine, but ensure we don't block the caller
        _ = Task.Run(async () =>
        {
            try
            {
                var queue = env.Destination.Discriminator;
                var elementLogger = Logger.WithContext("ElementName", elementName);
                
                elementLogger.LogDebug($"[{element.Config.Name}] ActiveMQ Manager received message for {queue}");

                if (env.Payload is byte[] bytes)
                {
                    await element.WriteAsync(bytes, queue);
                }
                else
                {
                    await element.WriteAsync(env.Payload.ToJson(), queue);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Background task failed in ActiveMqManager");
            }
        }, ct);

        await Task.CompletedTask;
    }
}