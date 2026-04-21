using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;

namespace Device.HostComm;

public class FileMessageDeviceManager : DeviceManagerBase<FileMessageDevice>
{
    public FileMessageDeviceManager(IMessageBus bus, List<IDeviceConfig> configs,
        IFireLogger<DeviceManagerBase<FileMessageDevice>> logger,
        Func<IDeviceConfig, IFireLogger, FileMessageDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory)
    {
    }

    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not FileMessageDevice device || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("DeviceName", device.Config.Name)
              .Verbose("[{Dev}] Forwarding file message to bus: {Topic}", device.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var topic = envelope.Destination;
        if (DeviceInstances.TryGetValue(topic.DeviceName, out var device))
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                string payload = envelope.Payload?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(payload))
                {
                    await device.WriteFileAsync(payload);
                }
            }
            catch (Exception ex)
            {
                device.GetLogger().Error(ex, "[{Dev}] Error writing message to outbound file", device.Config.Name);
            }
        }
    }
}
