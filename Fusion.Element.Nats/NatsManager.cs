using System.Collections.Concurrent;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Nats;

public class NatsManager : ElementManagerBase<NatsElement>
{
    private readonly ConcurrentDictionary<string, List<string>> _subjectToBusMap = new();

    public NatsManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<NatsManager> logger,
        Func<IElementBlueprint, IFireLogger, NatsElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task RegisterElementSourceBonds(IElement element)
    {
        var elementLogger = Logger.WithContext("ElementName", element.Key.ElementName);
        var bonds = ConfigurationLoader.GetAllReactionConfig()
            .SelectMany(w => w.Bonds)
            .Where(r => r.Source.StartsWith(element.Config.Name) && r.Mode > 0)
            .ToList();

        elementLogger.Information("[{Dev}] NATS Manager initializing Source {Count} Bonds", element.Key.ElementName, bonds.Count);

        foreach (var bond in bonds)
        {
            elementLogger.Information("[{Dev}] NATS Manager initializing Bond: {BondName}", element.Config.Name, bond.Name);
            var subjectName = new MessageBusTopic(bond.Source).Discriminator;
            if (element is not NatsElement natsDev) continue;

            // Subscribe to NATS subject and bond back to manager's common handler
            await natsDev.SubscribeAsync(subjectName, OnElementMessageToMessageBusAsync);

            if (_subjectToBusMap.TryGetValue(subjectName, out var list))
            {
                list.Add(bond.Source);
            }
            else
            {
                _subjectToBusMap[subjectName] = new List<string> { bond.Source };
            }
        }
    }

    protected override Task OnElementMessageToMessageBusAsync(object? message, object sender)
    {
        if (sender is not string subject || message is not string payload) return Task.CompletedTask;

        if (_subjectToBusMap.TryGetValue(subject, out var busTopics))
        {
            foreach (var path in busTopics)
            {
                var topic = new MessageBusTopic(path);
                Logger.LogDebug("[{Dev}] NATS Manager forwarding message to {Topic}: {Payload}", subject, topic, payload);
                MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, payload));
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var elementName = envelope.Destination.ElementName;
        if (!ElementInstances.TryGetValue(elementName, out var element))
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", elementName);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var subject = envelope.Destination.Discriminator;
                var payload = envelope.GetPayloadText();
                await element.PublishAsync(subject, payload);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Background task failed in NatsManager for topic {Topic}", envelope.Destination);
            }
        }, ct);

        await Task.CompletedTask;
    }
}
