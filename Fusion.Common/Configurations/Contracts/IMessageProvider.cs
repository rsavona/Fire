namespace Fusion.Common.Contracts;


public interface IMessageProvider
{
    // Any element that "is" an IMessageProvider must have this event
    event  Func<object, object, Task> MessageReceived;
}