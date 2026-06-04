using Fusion.Common.Contracts;

namespace Fusion.Element.Notification.Suite;

public interface INotificationElement : IElement
{
    Task SendAsync(string message, CancellationToken token, bool fireEvent = true);
}
