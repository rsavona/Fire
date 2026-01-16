using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common.BaseClasses;

public abstract record DeviceMessageBase : IDeviceMessage
{
  
    public string MessageType { get; set; } = "Unknown"; // Add this property


    protected DeviceMessageBase()
    {
      
    }

    public virtual MessageEnvelope WrapMessage(MessageBusTopic t, int gin = 0, string client = "")
    {
        return new MessageEnvelope(t, this, gin, client);
    }
}