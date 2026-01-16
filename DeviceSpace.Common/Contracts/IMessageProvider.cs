namespace DeviceSpace.Common.Contracts;


public interface IMessageProvider
{
    // Any device that "is" an IMessageProvider must have this event
    event Action<object, object> MessageReceived;
}