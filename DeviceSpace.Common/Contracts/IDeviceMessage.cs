using System.Collections.Concurrent;

namespace DeviceSpace.Common.Contracts;

public interface IDeviceMessage
{
    MessageEnvelope WrapMessage(MessageBusTopic t , int gin = 0, string client = "");
    
}

