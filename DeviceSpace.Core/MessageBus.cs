using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace DeviceSpace.Core;

public class MessageBus : IMessageBus
{
    public enum BusState
    {
        Idle,
        Processing,
        Faulted
    }

    public enum BusEvent
    {
        ActivityStarted,
        Publishing,
        FaultOccurred,
        FaultCleared
    }

    private readonly BusAuditLogger _auditLogger;
    private readonly ILogger _logger;

    // Using ConcurrentDictionary as a Set (Delegate is key, byte is a dummy value)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Delegate, byte>> _subscriptions;
    private readonly ConcurrentDictionary<Delegate, byte> _globalSubscribers;

    private readonly DeviceStatusTracker<BusState, BusEvent> _tracker;
    private string _lastErrorComment = "System Online";

    private int statusCounter = 0;

    public MessageBus(BusAuditLogger auditLogger, ILogger logger)
    {
        _auditLogger = auditLogger;
        _logger = logger.ForContext("DeviceName", "MesageBus");
        _tracker = new DeviceStatusTracker<BusState, BusEvent>(BusState.Idle, BusEvent.ActivityStarted);

        _subscriptions =
            new ConcurrentDictionary<string, ConcurrentDictionary<Delegate, byte>>(StringComparer.OrdinalIgnoreCase);
        _globalSubscribers = new ConcurrentDictionary<Delegate, byte>();
    }


    // ---------------------------------------------------------------------
    //                         SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    public Task<bool> SubscribeAsync(string topic, Delegate handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var handlers = _subscriptions.GetOrAdd(topic, _ => new ConcurrentDictionary<Delegate, byte>());
        bool added = handlers.TryAdd(handler, 0);
        _tracker.IncrementConnections();
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
                _logger.Information("UNSUBSCRIBED: {HandlerName} from topic '{Topic}'", name, topic);

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
    public Task PublishAsync(string topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default)
    {
        _tracker.Update(BusState.Processing, BusEvent.Publishing, _lastErrorComment);
        _tracker.IncrementInbound();
        _auditLogger.LogMessage(messageEnvelope.Payload, topic);
        if (statusCounter == 10)
        {
            statusCounter = 0;
            PublishOwnStatusAsync();
        }

        statusCounter++;

        return DispatchMessageInternal(topic, messageEnvelope, cancellationToken);
    }

    /// <summary>
    /// Publishes using the strongly-typed MessageBusTopic.
    /// </summary>
    public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default)
    {
        return PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);
    }

    public void SubscribeToAllAsync(Func<string, MessageEnvelope, CancellationToken, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _globalSubscribers.TryAdd(handler, 0);

        string name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.Information("GLOBAL SUBSCRIPTION: '{HandlerName}' is listening to all topics.", name);
    }

    public void UnsubscribeFromAll(Delegate handler)
    {
        if (handler != null) _globalSubscribers.TryRemove(handler, out _);
    }

    private Task DispatchStatusInternal(string busTopic, string envelopeTopic, IDeviceStatus status,
        CancellationToken ct)
    {
        if (status == null) return Task.CompletedTask;

        var envelope = new MessageEnvelope(envelopeTopic, status);
        return DispatchMessageInternal(busTopic, envelope, ct);
    }


    public Task PublishStatusAsync(DeviceStatusMessage snapshot, CancellationToken cancellationToken = default)
    {
        var topic = MessageBusTopic.DeviceStatus.ToString();
        return DispatchStatusInternal(topic, topic, snapshot, cancellationToken);
    }

// 3. Overload: Specific Topic
    public Task PublishStatusAsync(string topic, DeviceStatusMessage snapshot, CancellationToken ct)
    {
        return DispatchStatusInternal(topic, topic, snapshot, ct);
    }

// 4. Overload: Device Key 
    public Task PublishStatusAsync(string keyDeviceName, IDeviceStatus status)
    {
        // Preserving your original logic: Envelope gets the device key, but it publishes to the "DeviceStatus" topic
        return DispatchStatusInternal("DeviceStatus", keyDeviceName, status, CancellationToken.None);
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
        var allTasks = new List<Task>();

        // Check Verbose status once at the start of dispatch
        bool isVerbose = _logger.IsEnabled(Serilog.Events.LogEventLevel.Verbose);

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

                System.Diagnostics.Stopwatch? sw = null;
                string handlerName =
                    isVerbose ? $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}" : string.Empty;

                if (isVerbose)
                {
                    _logger.Verbose(">>> BUS ENTRY: Topic={Topic} Handler={Handler}", topic, handlerName);
                    sw = System.Diagnostics.Stopwatch.StartNew();
                }

                try
                {
                    Task handlerTask;
                    if (handler is Func<MessageEnvelope, CancellationToken, Task> envelopeHandler)
                    {
                        handlerTask = envelopeHandler(envelope, token);
                    }
                    else
                    {
                        // This is your dynamic invocation block (where the arg count check happens)
                        var methodParams = handler.Method.GetParameters();
                        object[] args = methodParams.Length switch
                        {
                            1 => new object[] { envelope },
                            2 => new object[] { envelope, token },
                            _ => new object[] { envelope }
                        };

                        handlerTask = (Task)handler.DynamicInvoke(args)!;
                    }

                    // Add timing to the Task continuation if it's Verbose
                    if (isVerbose && sw != null)
                    {
                        allTasks.Add(handlerTask.ContinueWith(t =>
                        {
                            sw.Stop();
                            _logger.Verbose("<<< BUS EXIT: Topic={Topic} Handler={Handler} | Elapsed={Elapsed}ms",
                                topic, handlerName, sw.ElapsedMilliseconds);

                            if (t.IsFaulted) HandleHandlerFailure(t, topic, envelope);
                        }));
                    }
                    else
                    {
                        allTasks.Add(handlerTask.ContinueWith(t => HandleHandlerFailure(t, topic, envelope),
                            TaskContinuationOptions.OnlyOnFaulted));
                    }

                    _tracker.IncrementOutbound();
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
        _logger.Error(ex, "Error handling message on topic '{Topic}'", originalTopic);
        PublishErrorInternal(originalTopic, originalMessage, ex);
    }

    private Task PublishErrorInternal(string originalTopic, MessageEnvelope originalMessage, Exception? ex)
    {
        _lastErrorComment = $"[{originalTopic}] {ex?.Message ?? "Unknown Error"}";
        _tracker.IncrementError(_lastErrorComment);
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
            _logger.Error(errorPayload.ToString());
            PublishOwnStatusAsync();
            var errorEnvelope = new MessageEnvelope(MessageBusTopic.InternalError, errorPayload);
            _ = PublishAsync(MessageBusTopic.InternalError.ToString(), errorEnvelope, CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.Error(logEx, "Failed to publish error message.");
        }

        return Task.CompletedTask;
    }

    private void LogSubscription(string topic, Delegate handler)
    {
        string name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.Information("Subscribed to topic '{Topic}' with {HandlerName}", topic, name);
    }

    public List<string> GetActiveTopics() => _subscriptions.Keys.ToList();

    /// <summary>
    /// Publishes a device status snapshot to the bus on a specific topic.
    /// </summary>
    /// <param name="status"></param>
    /// <param name="token"></param>
    public Task PublishStatusAsync(IDeviceStatus status, CancellationToken token)
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

    private Task PublishOwnStatusAsync()
    {
        try
        {
            DeviceKey busKey = new DeviceKey("System", "MessageBus");
            // Just grab the literal string directly from the tracker
            var stateText = _tracker.ToStatusMessage(busKey, "");

            // Pack the raw string into the envelope
            var envelope = new MessageEnvelope(MessageBusTopic.DeviceStatus, stateText);

            // Dispatch internally to bypass the audit log and main counters
            return DispatchMessageInternal(MessageBusTopic.DeviceStatus.ToString(), envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to publish internal bus status.");
            return Task.CompletedTask;
        }
    }
}