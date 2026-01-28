
namespace DeviceSpace.Common;

public record MessageBusTopic
{
    public static readonly MessageBusTopic DeviceStatus = new MessageBusTopic("All_Devices", "StatusMessage");
    public static readonly MessageBusTopic InternalError = new MessageBusTopic("All_Devices", "Exceptions");
    public static readonly MessageBusTopic Discovery = new MessageBusTopic("All_Devices", "DiagDiscovery");
    
    public readonly string DeviceName;
    public readonly string MessageType;
    public readonly string Discriminator;
    
    public MessageBusTopic(string deviceName, string messageType, string discriminator = "")
    {
        DeviceName = deviceName;
        MessageType = messageType;
        Discriminator = discriminator;

    }
    
    public MessageBusTopic(string strTopic) 
    {
        if (string.IsNullOrEmpty(strTopic))
        {
            throw new ArgumentException("Topic can not be empty");
        }
        var parts = strTopic.ToUpper().Split('.');
        DeviceName = parts[0];
        MessageType = parts[1];

        // Join everything from index 2 to the end using "." as the separator
        Discriminator = string.Join(".", parts.Skip(2));

    }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Discriminator)
            ? $"{DeviceName}.{MessageType}"
            : $"{DeviceName}.{MessageType}.{Discriminator}";
    }
}