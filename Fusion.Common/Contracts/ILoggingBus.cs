using System;
using System.Threading;
using System.Threading.Tasks;
using Fusion.Common.Logging;

namespace Fusion.Common.Contracts;

/// <summary>
/// A specialized, high-performance bus for structured log messages using System.Threading.Channels.
/// </summary>
public interface ILoggingBus
{
    /// <summary>
    /// Asynchronously publishes a structured log message to the bus.
    /// </summary>
    Task PublishAsync(LogMessage logMessage, CancellationToken ct = default);

    /// <summary>
    /// Subscribes a handler to a specific log topic (e.g. "LOG.INFO.TPNA2").
    /// </summary>
    Task SubscribeAsync(string topicPattern, Func<LogMessage, CancellationToken, Task> handler);
}
