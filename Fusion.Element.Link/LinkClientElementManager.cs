using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Link;

/// <summary>
/// Manages LinkClientElement instances. Remote envelopes received over the link
/// are republished onto the local bus; local envelopes matching the element's
/// PublishTopics patterns are forwarded to the remote instance.
/// </summary>
public class LinkClientElementManager : ElementManagerBase<LinkClientElement>
{
    public LinkClientElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<LinkClientElement>> logger,
        Func<IElementBlueprint, IFireLogger, LinkClientElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Republishes envelopes that arrived from the remote instance onto the local bus.
    /// </summary>
    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not LinkClientElement element || messEnv is not MessageEnvelope envelope) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Verbose("[{Dev}] Republishing remote envelope to local bus: {Topic}",
                  element.Config.Name, envelope.Destination);

        await MessageBus.PublishAsync(envelope.Destination.ToString(), envelope);
    }

    /// <summary>
    /// Subscribes to each configured PublishTopics pattern and forwards matching
    /// local envelopes over the link to the remote instance.
    /// </summary>
    protected override Task RegisterElementSourceBonds(IElement element)
    {
        if (element is not LinkClientElement link) return Task.CompletedTask;

        foreach (string pattern in link.PublishTopics)
        {
            Func<MessageEnvelope, CancellationToken, Task> handler = async (envelope, ct) =>
            {
                // Loop guard: never push an envelope that itself arrived over a link.
                if (LinkConventions.IsFromLink(envelope)) return;
                if (!link.IsConnected) return;

                try
                {
                    await link.SendPublishAsync(envelope, ct);
                }
                catch (Exception ex)
                {
                    link.GetLogger().Error(ex, "[{Dev}] Failed to forward {Topic} to remote instance",
                        link.Config.Name, envelope.Destination);
                }
            };

            MessageBus.SubscribeAsync(pattern, handler);
            Logger.Information("[{Dev}] Forwarding local topic pattern {Pattern} to remote instance",
                link.Config.Name, pattern);
        }

        return Task.CompletedTask;
    }
}
