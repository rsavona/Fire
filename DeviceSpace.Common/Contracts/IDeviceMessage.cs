using System.Collections.Concurrent;

namespace DeviceSpace.Common.Contracts;

public interface IDeviceMessage
{   
    /// <summary>
    /// Wraps the message in a MessageEnvelope object.
    /// </summary>
    /// <param name="t"></param>
    /// <param name="gin"></param>
    /// <param name="client"></param>
    /// <returns></returns>
    MessageEnvelope WrapMessage(MessageBusTopic t , int gin = 0, string client = "");
    /// <summary>
    /// Outputs the message content to the logging system.
    /// </summary>
    void LogMessage();

    /// <summary>
    /// Serializes the message object into a JSON string for transport.
    /// </summary>
    string ToJson();
}

