using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceSpace.Common.Contracts;

/// <summary>
/// Defines a contract for a Message Bus used for decoupled communication 
/// within the application, typically for a Warehouse Control System.
/// </summary>
public interface IMessageBus
{
    // --- Message Envelope Placeholder ---
    // NOTE: For this interface to compile, you must have a 'MessageEnvelope' class/struct defined.
    // For simplicity, we are assuming it exists and holds the message data and metadata.

    // ---------------------------------------------------------------------
    //                         ASYNCHRONOUS SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Subscribes an async handler to a specific topic.
    /// The handler receives the MessageEnvelope and a CancellationToken.
    /// </summary>
    /// <param name="topic">The topic/route name.</param>
    /// <param name="handler">The async function to execute upon message receipt.</param>
    Task<bool> SubscribeAsync(string topic, Delegate handler);

    /// <summary>
    /// Subscribes an async handler to a specific topic, using a type-safe message payload.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message payload expected.</typeparam>
    /// <param name="topic">The topic/route name.</param>
    /// <param name="handler">The async function that receives the message payload.</param>
    Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler);

    /// <summary>
    /// Subscribes an async request/response handler to a specific topic (e.g., Command/Query).
    /// </summary>
    /// <typeparam name="TRequest">The type of the request message.</typeparam>
    /// <typeparam name="TResponse">The type of the expected response.</typeparam>
    /// <param name="topic">The topic/route name.</param>
    /// <param name="handler">The async function that receives a request and returns a response.</param>
    Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler);

    // ---------------------------------------------------------------------
    //                           UNSUBSCRIBING
    // ---------------------------------------------------------------------

    /// <summary>
    /// Unsubscribes a previously registered handler based on its topic and type signature.
    /// </summary>
    /// <param name="topic">The topic/route name.</param>
    /// <param name="handler">The specific handler function to remove.</param>
    void Unsubscribe(string topic, Delegate handler);

    // ---------------------------------------------------------------------
    //                              PUBLISHING
    // ---------------------------------------------------------------------

    /// <summary>
    /// Publishes a message to all subscribers of its topic.
    /// </summary>
    /// <param name="topic">The topic/route name.</param>
    /// <param name="messageEnvelope">The complete message envelope to publish.</param>
    /// <param name="cancellationToken">Token to observe for cancellation requests.</param>
    /// <returns>A Task representing the asynchronous publish operation.</returns>
    Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default);

    Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default);

    Task PublishStatusAsync(DeviceStatusMessage snapshot, CancellationToken cancellationToken = default);


    // ---------------------------------------------------------------------
    //                              DIAGNOSTICS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Gets a list of all currently known topics with active subscriptions.
    /// </summary>
    List<string> GetActiveTopics();
    List<string> GetSubscriptionList(MessageBusTopic messageBusTopic);
    List<string> GetSubscriptionList(string messageBusTopic);
}
    