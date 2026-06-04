
namespace Fusion.Common;

public record MessageBusTopic
{
    public static readonly MessageBusTopic ElementStatus = new MessageBusTopic("All_Elements", "StatusMessage");
    public static readonly MessageBusTopic InternalError = new MessageBusTopic("All_Elements", "Exceptions");
    public static readonly MessageBusTopic Discovery = new MessageBusTopic("All_Elements", "DiagDiscovery");
    public static readonly MessageBusTopic SystemControl = new MessageBusTopic("System", "Control");
    public static readonly MessageBusTopic SystemTopology = new MessageBusTopic("System", "Topology");
    public static readonly MessageBusTopic ConsoleCommand = new MessageBusTopic("System", "ConsoleCommand");
    public static readonly MessageBusTopic DataFlow = new MessageBusTopic("System", "DataFlow");
    
    public readonly string ElementName;
    public readonly string MessageType;
    public readonly string Discriminator;
    
    public MessageBusTopic(string elementName, string messageType, string discriminator = "")
    {
        ElementName = elementName;
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
        ElementName = parts[0];
        
        if (parts.Length > 1)
        {
            MessageType = parts[1];
            // Join everything from index 2 to the end using "." as the separator
            Discriminator = string.Join(".", parts.Skip(2));
        }
        else
        {
            MessageType = "DEFAULT";
            Discriminator = string.Empty;
        }
    }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Discriminator)
            ? $"{ElementName}.{MessageType}"
            : $"{ElementName}.{MessageType}.{Discriminator}";
    }
}