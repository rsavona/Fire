using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Device.HostComm;

public class FileMessageElementManager : ElementManagerBase<FileMessageElement>
{
    public FileMessageElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<FileMessageElement>> logger,
        Func<IElementBlueprint, IFireLogger, FileMessageElement> deviceFactory,
        string managerName)
        : base(bus, configs, logger, deviceFactory, managerName)
    {
    }

    protected override async Task OnDeviceMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not FileMessageElement device || messEnv is not MessageEnvelope env) return;

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
