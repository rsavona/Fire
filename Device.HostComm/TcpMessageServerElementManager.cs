using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Device.HostComm;

/// <summary>
/// Manages one or more TcpMessageServerElement instances and routes their messages to the Message Bus.
/// </summary>
public class TcpMessageServerElementManager : ElementManagerBase<TcpMessageServerElement>
{
    public TcpMessageServerElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<TcpMessageServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, TcpMessageServerElement> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards parsed messages from the element directly to the message bus.
    /// </summary>
    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not TcpMessageServerElement device || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("DeviceName", device.Config.Name)
              .Verbose("[{Dev}] Forwarding message to bus: {Topic}", device.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles messages from the message bus and sends them to the appropriate element/client.
    /// Expects the payload to be the raw string or object to send.
    /// </summary>
    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        
        if (DeviceInstances.TryGetValue(topic.DeviceName, out var device))
        {
            device.GetLogger().LogDebug("[{Dev}] Received bus message for topic: {Topic}", device.Config.Name,envelope.Payload?.ToString() ?? string.Empty );
            try
            {
                ct.ThrowIfCancellationRequested();
                
                string payload = envelope.Payload?.ToString() ?? string.Empty;
                
                if (!string.IsNullOrEmpty(envelope.Client))
                {
                    await device.SendAsync(envelope.Client, payload, ct);
                }
                else
                {
                    if ( await device.SendAsync(payload, ct))
                        device.Tracker.IncrementOutbound();
                }
                device.GetLogger().LogDebug("[{Dev}] Sent bus message to client: {Client}", device.Config.Name, envelope.Client);
            }
            catch (Exception ex)
            {
                device.GetLogger().Error(ex, "[{Dev}] Error sending message to client", device.Config.Name);
            }
        }
    }
}
