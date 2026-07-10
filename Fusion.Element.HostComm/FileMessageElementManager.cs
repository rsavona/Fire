using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.HostComm;

public class FileMessageElementManager : ElementManagerBase<FileMessageElement>
{
    public FileMessageElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<FileMessageElement>> logger,
        Func<IElementBlueprint, IFireLogger, FileMessageElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not FileMessageElement element || messEnv is not MessageEnvelope env) return;

        Logger.WithContext("ElementName", element.Config.Name)
              .Verbose("[{Dev}] Forwarding file message to bus: {Topic}", element.Config.Name, env.Destination);

        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

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
            ct.ThrowIfCancellationRequested();
            string payload = envelope.GetPayloadText();
            if (!string.IsNullOrEmpty(payload))
            {
                await element.WriteFileAsync(payload);
            }
        }
        catch (Exception ex)
        {
            element.GetLogger().Error(ex, "[{Dev}] Error writing message to outbound file", element.Config.Name);
        }
    }
}
