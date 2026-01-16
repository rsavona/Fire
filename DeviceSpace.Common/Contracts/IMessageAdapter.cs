
using System;

namespace DeviceSpace.Common.Contracts;

/// <summary>
/// The contract for all hot-pluggable transformer plugins.
/// </summary>
public interface IMessageAdapter
{
    /// <summary>
    /// The C# Type of the message this transformer accepts as input.
    /// </summary>
    Type SourceType { get; }

    /// <summary>
    /// The C# Type of the message this transformer produces as output.
    /// </summary>
    Type TargetType { get; }

    /// <summary>
    /// Performs the transformation from the source message to the target message.
    /// </summary>
    MessageEnvelope Adapt(MessageEnvelope sourceMessageEnvelope);
}
