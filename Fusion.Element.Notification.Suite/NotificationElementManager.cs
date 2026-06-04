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
        if (ElementInstances.TryGetValue(elementName, out var element))
        {
            try
            {
                var payload = envelope.Payload?.ToString() ?? string.Empty;
                await element.SendAsync(payload, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{Dev}] Failed to process notification message", elementName);
            }
        }
    }
}
