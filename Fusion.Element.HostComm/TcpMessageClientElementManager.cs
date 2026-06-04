using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.HostComm;

/// <summary>
/// Manages one or more TcpMessageClientElement instances and bonds their messages to the Message Bus.
/// </summary>
public class TcpMessageClientElementManager : ElementManagerBase<TcpMessageClientElement>
{
    public TcpMessageClientElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<TcpMessageClientElement>> logger,
        Func<IElementBlueprint, IFireLogger, TcpMessageClientElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards parsed messages from the element directly to the message bus.
    /// </summary>
    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not TcpMessageClientElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Verbose("[{Dev}] Forwarding message to bus: {Topic}", element.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles messages from the message bus and sends them to the appropriate element.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (ElementInstances.TryGetValue(topic.ElementName, out var element))
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                
                string payload = envelope.Payload?.ToString() ?? string.Empty;
                
                // Append ETX if requested or required by protocol
                if (!payload.EndsWith("\u0003"))
                {
                    payload += "\u0003";
                }

                await element.SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                element.GetLogger().Error(ex, "[{Dev}] Error sending message to server", element.Config.Name);
            }
        }
    }
}
