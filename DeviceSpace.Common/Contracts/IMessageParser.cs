namespace DeviceSpace.Common.Contracts;

public interface IMessageParser
{
    // Returns true if this parser can handle a specific queue/topic
    
    bool CanHandle(SourceIdentifier source);
    
    // The logic to turn the raw string into a clean object
    object Parse(string rawPayload);
}