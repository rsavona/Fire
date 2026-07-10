using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Sparkplug;

/// <summary>
/// Manages SparkplugEdgeNodeElement instances. Wires each element into the
/// local bus: the global element status topic feeds device discovery and
/// metrics, and each blueprint-selected MetricTopics pattern feeds additional
/// device metrics.
/// </summary>
public class SparkplugElementManager : ElementManagerBase<SparkplugEdgeNodeElement>
{
    public SparkplugElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<SparkplugEdgeNodeElement>> logger,
        Func<IElementBlueprint, IFireLogger, SparkplugEdgeNodeElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override Task RegisterElementSourceBonds(IElement element)
    {
        if (element is not SparkplugEdgeNodeElement node) return Task.CompletedTask;

        if (node.PublishStatusMetrics)
        {
            MessageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(),
                (Func<MessageEnvelope, CancellationToken, Task>)node.HandleStatusEnvelopeAsync);
            Logger.Information("[{Dev}] Mirroring element status traffic to Sparkplug devices under spBv1.0/{Group}/{Node}",
                node.Config.Name, node.GroupId, node.EdgeNodeId);
        }

        foreach (string pattern in node.MetricTopics)
        {
            MessageBus.SubscribeAsync(pattern,
                (Func<MessageEnvelope, CancellationToken, Task>)node.HandleMetricEnvelopeAsync);
            Logger.Information("[{Dev}] Bus topic pattern {Pattern} mapped to Sparkplug device metrics",
                node.Config.Name, pattern);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Bus messages addressed directly to the element become node-level metrics
    /// in the next NDATA batch.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (!ElementInstances.TryGetValue(topic.ElementName, out var element))
        {
            Logger.Warning("[{Dev}] Received bus message but element instance not found.", topic.ElementName);
            return;
        }

        try
        {
            await element.SendAsync(envelope.GetPayloadText(), ct);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to queue message for Sparkplug publish", element.Config.Name);
        }
    }
}
