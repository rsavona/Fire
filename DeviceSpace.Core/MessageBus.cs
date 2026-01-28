using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using System.Reflection;

namespace DeviceSpace.Core;

public class MessageBus : IMessageBus
{
    private readonly BusAuditLogger _auditLogger;
    private readonly ILogger _logger;

    // Using ConcurrentDictionary as a Set (Delegate is key, byte is a dummy value)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Delegate, byte>> _subscriptions;
    private readonly ConcurrentDictionary<Delegate, byte> _globalSubscribers;

    private long _messagesPublished = 0;
    private long _messagesFailed = 0;

    public MessageBus(BusAuditLogger auditLogger, ILogger<MessageBus> logger)
    {
        _auditLogger = auditLogger;
        _logger = logger;
        _subscriptions =
            new ConcurrentDictionary<string, ConcurrentDictionary<Delegate, byte>>(StringComparer.OrdinalIgnoreCase);
        _globalSubscribers = new ConcurrentDictionary<Delegate, byte>();
    }

    public (long Published, long Failed) GetMetrics()
    {
        return (Interlocked.Read(ref _messagesPublished), Interlocked.Read(ref _messagesFailed));
    }

    // ---------------------------------------------------------------------
    //                         SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    public Task<bool> SubscribeAsync(string topic, Delegate handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var handlers = _subscriptions.GetOrAdd(topic, _ => new ConcurrentDictionary<Delegate, byte>());
        bool added = handlers.TryAdd(handler, 0);

        if (added) LogSubscription(topic, handler);
        return Task.FromResult(added);
    }

    public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler)
    {
        return SubscribeAsync(topic, (Delegate)handler);
    }

    public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler)
    {
        return SubscribeAsync(topic, (Delegate)handler);
    }

    /// <summary>
    /// Removes a specific handler from a topic.
    /// </summary>
    public void Unsubscribe(string topic, Delegate handler)
    {
        if (handler == null) return;

        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            if (handlers.TryRemove(handler, out _))
            {
                string name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
                _logger.LogInformation("UNSUBSCRIBED: {HandlerName} from topic '{Topic}'", name, topic);

                // Optional: Clean up the topic key if no listeners remain
                if (handlers.IsEmpty) _subscriptions.TryRemove(topic, out _);
            }
        }
    }

    // ---------------------------------------------------------------------
    //                              PUBLISHING
    // ---------------------------------------------------------------------

    /// <summary>
    /// Core publish method. Logs to audit trail and dispatches to listeners.
    /// </summary>
    public Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
    {
        _auditLogger.LogMessage(messageEnvelope.Payload, topic);
        return DispatchMessageInternal(topic, messageEnvelope, cancellationToken);
    }

    /// <summary>
    /// Publishes using the strongly-typed MessageBusTopic.
    /// </summary>
    public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
    {
        return PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);
    }

    /// <summary>
    /// Publishes a general status snapshot to the default DeviceStatus topic.
    /// </summary>
    public Task PublishStatusAsync(DeviceStatusMessage snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot == null) return Task.CompletedTask;

        var topic = MessageBusTopic.DeviceStatus.ToString();
        var envelope = new MessageEnvelope(topic, snapshot);
        
        return PublishAsync(topic, envelope, cancellationToken);
    }

    public void SubscribeToAllAsync(Func<string, MessageEnvelope, CancellationToken, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _globalSubscribers.TryAdd(handler, 0);

        string name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.LogInformation("GLOBAL SUBSCRIPTION: '{HandlerName}' is listening to all topics.", name);
    }

    public void UnsubscribeFromAll(Delegate handler)
    {
        if (handler != null) _globalSubscribers.TryRemove(handler, out _);
    }

   public Task PublishStatusAsync(string topic, DeviceStatusMessage snapshot, CancellationToken ct)
    {
        var envelope = new MessageEnvelope(topic, snapshot);
        return PublishAsync(topic, envelope, ct);
    }

    public Task PublishStatusAsync(string keyDeviceName, IDeviceStatus status)
    {
        var envelope = new MessageEnvelope(keyDeviceName, status);
        // Usually published to a general status topic or a device-specific one
        return PublishAsync("DeviceStatus", envelope);
    }

    public List<string> GetSubscriptionList(string topic)
    {
        var listeners = new List<string>();

        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            foreach (var handler in handlers.Keys)
            {
                listeners.Add($"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
            }
        }

        foreach (var global in _globalSubscribers.Keys)
        {
            listeners.Add($"[GLOBAL] {global.Method.DeclaringType?.Name}.{global.Method.Name}");
        }

        return listeners;
    }

 
    public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic)
     {
         return GetSubscriptionList(messageBusTopic.ToString());
     }
     
    public List<string> GetSubscriptionList()
    {
        var lines = new List<string>();
        foreach (var sub in _subscriptions)
        {
            lines.Add($"Topic: {sub.Key}");
            foreach (var handler in sub.Value.Keys)
            {
                lines.Add($"  - Handler: {handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
            }
        }

        return lines;
    }

    // ---------------------------------------------------------------------
    //                         PUBLISHING
    // ---------------------------------------------------------------------


    private Task DispatchMessageInternal(string topic, MessageEnvelope envelope, CancellationToken token = default)
    {
        Interlocked.Increment(ref _messagesPublished);
        var allTasks = new List<Task>();

        // 1. Global Subscribers
        foreach (var globalHandler in _globalSubscribers.Keys)
        {
            if (token.IsCancellationRequested) break;
            if (globalHandler is Func<string, MessageEnvelope, CancellationToken, Task> typedGlobal)
            {
                allTasks.Add(typedGlobal(topic, envelope, token)
                    .ContinueWith(t => HandleHandlerFailure(t, topic, envelope),
                        TaskContinuationOptions.OnlyOnFaulted));
            }
        }

        // 2. Topic Subscribers
        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            foreach (var handler in handlers.Keys)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (handler is Func<MessageEnvelope, CancellationToken, Task> envelopeHandler)
                    {
                        allTasks.Add(envelopeHandler(envelope, token)
                            .ContinueWith(t => HandleHandlerFailure(t, topic, envelope),
                                TaskContinuationOptions.OnlyOnFaulted));
                    }
                    else
                    {
                        // Handle Dynamic Invocations (Generic T handlers)
                        var handlerTask = (Task)handler.DynamicInvoke(envelope)!;
                        allTasks.Add(handlerTask.ContinueWith(t => HandleHandlerFailure(t, topic, envelope),
                            TaskContinuationOptions.OnlyOnFaulted));
                    }
                }
                catch (Exception ex)
                {
                    PublishErrorInternal(topic, envelope, ex);
                }
            }
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    //                         ERROR HANDLING
    // ---------------------------------------------------------------------

    private void HandleHandlerFailure(Task faultedTask, string originalTopic, MessageEnvelope originalMessage)
    {
        var ex = faultedTask.Exception?.InnerException ?? faultedTask.Exception;
        _logger.LogError(ex, "Error handling message on topic '{Topic}'", originalTopic);
        PublishErrorInternal(originalTopic, originalMessage, ex);
    }

    private Task PublishErrorInternal(string originalTopic, MessageEnvelope originalMessage, Exception? ex)
    {
        Interlocked.Increment(ref _messagesFailed);
        try
        {
            var errorPayload = new BusErrorMessage
            {
                OriginalTopic = originalTopic,
                ExceptionMessage = ex?.Message ?? "Unknown Error",
                StackTrace = ex?.StackTrace,
                Timestamp = DateTime.Now, // Swapped to Local Time
                OriginalPayload = originalMessage.Payload
            };

            var errorEnvelope = new MessageEnvelope(MessageBusTopic.InternalError, errorPayload);
            _ = PublishAsync(MessageBusTopic.InternalError.ToString(), errorEnvelope, CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Failed to publish error message.");
        }

        return Task.CompletedTask;
    }

    private void LogSubscription(string topic, Delegate handler)
    {
        string name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.LogInformation("Subscribed to topic '{Topic}' with {HandlerName}", topic, name);
    }

    public List<string> GetActiveTopics() => _subscriptions.Keys.ToList();

    /// <summary>
    /// Publishes a device status snapshot to the bus on a specific topic.
    /// </summary>
    /// <param name="status"></param>
    /// <param name="token"></param>
    public Task PublishStatusAsync( IDeviceStatus status, CancellationToken token)
    {
        var envelope = new MessageEnvelope(new MessageBusTopic(MessageBusTopic.DeviceStatus.ToString()), status);
        return PublishAsync(MessageBusTopic.DeviceStatus.ToString(), envelope, token);
    }

    public int GetListenerCount(string topic)
    {
        int count = _globalSubscribers.Count;
        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            count += handlers.Count;
        }

        return count;
    }
}