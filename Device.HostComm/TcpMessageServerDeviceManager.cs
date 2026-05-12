using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;

namespace Device.HostComm;

/// <summary>
/// Manages one or more TcpMessageServerDevice instances and routes their messages to the Message Bus.
/// </summary>
public class TcpMessageServerDeviceManager : DeviceManagerBase<TcpMessageServerDevice>
{
    public TcpMessageServerDeviceManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<TcpMessageServerDevice>> logger,
        Func<IDeviceConfig, IFireLogger, TcpMessageServerDevice> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    /// <summary>
    /// Forwards parsed messages from the device directly to the message bus.
    /// </summary>
    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not TcpMessageServerDevice device || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("DeviceName", device.Config.Name)
              .Verbose("[{Dev}] Forwarding message to bus: {Topic}", device.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    /// <summary>
    /// Handles messages from the message bus and sends them to the appropriate device/client.
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
