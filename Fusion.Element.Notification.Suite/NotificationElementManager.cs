using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Notification.Suite;

public class NotificationElementManager : ElementManagerBase<INotificationElement>
{
    public NotificationElementManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<NotificationElementManager> logger,
        Func<IElementBlueprint, IFireLogger, INotificationElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var elementName = envelope.Destination.ElementName;
        if (!ElementInstances.TryGetValue(elementName, out var element))
        {
            Logger.LogWarning("[{Dev}] Received bus message but element instance not found.", elementName);
            return;
        }

        try
        {
            await element.SendAsync(envelope.GetPayloadText(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Dev}] Failed to process notification message", elementName);
        }
    }
}
