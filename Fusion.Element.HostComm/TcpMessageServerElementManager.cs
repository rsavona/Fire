using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.HostComm;

/// <summary>
/// Manages one or more TcpMessageServerElement instances and bonds their messages to the Message Bus.
/// </summary>
public class TcpMessageServerElementManager : ElementManagerBase<TcpMessageServerElement>
{
    public TcpMessageServerElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<TcpMessageServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, TcpMessageServerElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards parsed messages from the element directly to the message bus.
    /// </summary>
    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not TcpMessageServerElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Verbose("[{Dev}] Forwarding message to bus: {Topic}", element.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles messages from the message bus and sends them to the appropriate element/client.
    /// Expects the payload to be the raw string or object to send.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;

        if (!ElementInstances.TryGetValue(topic.ElementName, out var element))
        {
            Logger.Warning("[{Dev}] Received bus message but element instance not found.", topic.ElementName);
            return;
        }

        string payload = envelope.GetPayloadText();
        element.GetLogger().LogDebug("[{Dev}] Received bus message for topic: {Topic}", element.Config.Name, payload);
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(envelope.Client))
            {
                await element.SendAsync(envelope.Client, payload, ct);
            }
            else
            {
                if ( await element.SendAsync(payload, ct))
                    element.Tracker.IncrementOutbound();
            }
            element.GetLogger().LogDebug("[{Dev}] Sent bus message to client: {Client}", element.Config.Name, envelope.Client);
        }
        catch (Exception ex)
        {
            element.GetLogger().Error(ex, "[{Dev}] Error sending message to client", element.Config.Name);
        }
    }
}
