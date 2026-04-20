namespace DeviceSpace.Common.Contracts;

/// <summary>
/// Defines a parser capable of converting generic JbusMessage envelopes
/// into specific strongly-typed application messages.
/// </summary>
public interface IJBusMessageParser
{
    /// <summary>
    /// Determines if this parser can handle the given message type.
    /// </summary>
    bool CanParse(string messageType);

    /// <summary>
    /// Parses the JbusMessage envelope into a specific domain object.
    /// </summary>
    object? Parse(JBusMessage message);
}