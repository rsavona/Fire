using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Device.HostComm;

/// <summary>
/// Manages one or more TcpMessageClientElement instances and routes their messages to the Message Bus.
/// </summary>
public class TcpMessageClientElementManager : ElementManagerBase<TcpMessageClientElement>
{
    public TcpMessageClientElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<TcpMessageClientElement>> logger,
        Func<IElementBlueprint, IFireLogger, TcpMessageClientElement> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards parsed messages from the element directly to the message bus.
    /// </summary>
    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not TcpMessageClientElement device || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("DeviceName", device.Config.Name)
              .Verbose("[{Dev}] Forwarding message to bus: {Topic}", device.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles messages from the message bus and sends them to the appropriate element.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (DeviceInstances.TryGetValue(topic.DeviceName, out var device))
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

                await device.SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                device.GetLogger().Error(ex, "[{Dev}] Error sending message to server", device.Config.Name);
            }
        }
    }
}
