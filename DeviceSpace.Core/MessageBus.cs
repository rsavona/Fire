using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Core;

public class MessageBus : IMessageBus
{
    private readonly BusAuditLogger _auditLogger;
    private readonly ILogger _logger;
    
    private readonly ConcurrentDictionary<string, ConcurrentBag<Delegate>> _subscriptions;
    private readonly ConcurrentBag<Delegate> _globalSubscribers;
    private long _messagesPublished = 0;
    private long _messagesFailed = 0;

    public MessageBus(BusAuditLogger auditLogger, ILogger<MessageBus> logger)
    {
        _auditLogger = auditLogger;
        _logger = logger;
        _subscriptions = new ConcurrentDictionary<string, ConcurrentBag<Delegate>>();
        _globalSubscribers = new ConcurrentBag<Delegate>();
    }

    public (long Published, long Failed) GetMetrics()
    {
        return (Interlocked.Read(ref _messagesPublished), Interlocked.Read(ref _messagesFailed));
    }

    // ---------------------------------------------------------------------
    //                         SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public Task<bool> SubscribeAsync(string topic, Delegate handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        GetHandlersForTopic(topic).Add(handler);
        LogSubscription(topic, handler);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    /// <typeparam name="TMessage"></typeparam>
    /// <exception cref="ArgumentNullException"></exception>
    public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        GetHandlersForTopic(topic).Add(handler);
        LogSubscription(topic, handler);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <exception cref="ArgumentNullException"></exception>
    public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        GetHandlersForTopic(topic).Add(handler); 
        LogSubscription(topic, handler);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    /// <exception cref="NotImplementedException"></exception>
    public void Unsubscribe(string topic, Delegate handler)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Subscribes to ALL messages flowing through the bus, regardless of topic.
    /// Useful for Dashboards, Recorders, and Debugging.
    /// </summary>
    public void SubscribeToAllAsync(Func<string, MessageEnvelope, CancellationToken, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _globalSubscribers.Add(handler);
        
        string handlerName = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.LogInformation("GLOBAL SUBSCRIPTION: '{HandlerName}' is listening to all topics.", handlerName);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    private void LogSubscription(string topic, Delegate handler)
    {
        string handlerName = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.LogInformation("Subscribed to topic '{Topic}' with {HandlerName}", topic, handlerName);
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="topic"></param>
    /// <returns></returns>
    private ConcurrentBag<Delegate> GetHandlersForTopic(string topic)
    {
        return _subscriptions.GetOrAdd(topic, _ => new ConcurrentBag<Delegate>());
    }

    /// <summary>
    /// Publish a status message to the Status Topic.
    /// </summary>
    /// <param name="sourceDevice"></param>
    /// <param name="status"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task PublishStatusAsync(string sourceDevice, IDeviceStatus status, CancellationToken token )
    {
        var t = new MessageBusTopic(sourceDevice);
        var envelope = new MessageEnvelope(t, status);
        return DispatchMessageInternal(MessageBusTopic.DeviceStatus.ToString(), envelope, token);
    }

    /// <summary>
    ///     
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="messageEnvelope"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
    {
        _auditLogger.LogMessage( messageEnvelope.Payload, topic);
        return DispatchMessageInternal(topic, messageEnvelope, cancellationToken);
    }


   /// <summary>
   /// Dispatches a message to all subscribers.
   /// </summary>
   /// <param name="topic"></param>
   /// <param name="envelope"></param>
   /// <param name="token"></param>
   /// <returns></returns>
    private Task DispatchMessageInternal(string topic, MessageEnvelope envelope, CancellationToken token = default)
    {
        Interlocked.Increment(ref _messagesPublished);

        var allTasks = new List<Task>();

        //  Process GLOBAL Subscribers first (The Firehose)
        if (!_globalSubscribers.IsEmpty)
        {
            foreach (var globalHandler in _globalSubscribers)
            {
                if (token.IsCancellationRequested) break;

                // Global handlers signature: Func<string, MessageEnvelope, CancellationToken, Task>
                if (globalHandler is Func<string, MessageEnvelope, CancellationToken, Task> typedGlobal)
                {
                    allTasks.Add(typedGlobal(topic, envelope, token)
                        .ContinueWith(t => HandleHandlerFailure(t, topic, envelope), TaskContinuationOptions.OnlyOnFaulted));
                }
            }
        }

        if (_subscriptions.TryGetValue(topic, out var handlers) && !handlers.IsEmpty)
        {
            foreach (var handler in handlers)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    if (handler is Func<MessageEnvelope, CancellationToken, Task> envelopeHandler)
                    {
                        allTasks.Add(envelopeHandler(envelope, token)
                            .ContinueWith(t => HandleHandlerFailure(t, topic, envelope), TaskContinuationOptions.OnlyOnFaulted));
                    }
                    else if (handler.Method.ReturnType == typeof(Task) && handler.Method.GetParameters().Length == 1)
                    {
                        // Handle Generic <T> handlers (Reflection)
                        var payloadType = handler.Method.GetParameters()[0].ParameterType;
                        
                        var handlerTask = (Task)handler.DynamicInvoke(envelope)!;
                            allTasks.Add(handlerTask
                                .ContinueWith(t => HandleHandlerFailure(t, topic, envelope), TaskContinuationOptions.OnlyOnFaulted));
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

   
    /// <summary>
    ///   Handles a failed handler task by publishing an error message to the Error Topic.
    /// </summary>
    /// <param name="faultedTask"></param>
    /// <param name="originalTopic"></param>
    /// <param name="originalMessage"></param>
    private void HandleHandlerFailure(Task faultedTask, string originalTopic, MessageEnvelope originalMessage)
    {
        var ex = faultedTask.Exception?.InnerException ?? faultedTask.Exception;
        PublishErrorInternal(originalTopic, originalMessage, ex);
        _logger.LogError(ex, "Error handling message on topic '{Topic}'", originalTopic);
    }

    /// <summary>
    ///  
    /// </summary>
    /// <param name="originalTopic"></param>
    /// <param name="originalMessage"></param>
    /// <param name="ex"></param>
    /// <returns></returns>
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
                Timestamp = DateTime.UtcNow,
                OriginalPayload = originalMessage.Payload
            };

            var errorEnvelope = new MessageEnvelope(MessageBusTopic.InternalError, errorPayload);
            
            // Recursive call is safe due to the Topic check above
            PublishAsync(MessageBusTopic.InternalError.ToString(), errorEnvelope, CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "CRITICAL: Failed to publish to Error Topic.");
        }
        return  Task.CompletedTask;
    }

    public List<string> GetActiveTopics()
    {
        return _subscriptions.Keys.ToList();
    }
    
    /// <summary>
    ///     
    /// </summary>
    /// <param name="keyDeviceName"></param>
    /// <param name="status"></param>
    /// <returns></returns>
    public Task PublishStatusAsync(string keyDeviceName, IDeviceStatus status)
    {
        var envelope = new MessageEnvelope(MessageBusTopic.DeviceStatus, status);
        return  PublishAsync(MessageBusTopic.DeviceStatus.ToString(), envelope);
    }
}