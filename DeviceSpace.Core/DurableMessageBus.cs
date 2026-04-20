using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace DeviceSpace.Core;

public class DurableMessageBus : IMessageBus
{
    private readonly BusAuditLogger _auditLogger;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ConcurrentBag<Delegate>> _subscriptions;
    private readonly ConcurrentBag<Delegate> _globalSubscribers;
    private long _messagesPublished = 0;
    private long _messagesFailed = 0;
    private record MessageWorkItem(string Topic, MessageEnvelope Envelope);
    private readonly Channel<MessageWorkItem> _messageChannel;
    private readonly ConcurrentDictionary<Delegate, SubscriberHealth> _handlerHealth = new();

    public DurableMessageBus(BusAuditLogger auditLogger, ILogger<MessageBus> logger)
    {
        _auditLogger = auditLogger;
        _logger = logger;
        _subscriptions = new ConcurrentDictionary<string, ConcurrentBag<Delegate>>(StringComparer.OrdinalIgnoreCase);
        _globalSubscribers = new ConcurrentBag<Delegate>();

        _messageChannel = Channel.CreateBounded<MessageWorkItem>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        // Background worker to drain the channel
        Task.Run(async () => await ProcessMessagesAsync());
    }

    // ---------------------------------------------------------------------
    //                         SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    public Task<bool> SubscribeAsync(string topic, Delegate handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        GetHandlersForTopic(topic).Add(handler);
        LogSubscription(topic, handler);
        return Task.FromResult(true);
    }

    public Task<bool> SubscribeAsync(string topic, Func<MessageEnvelope, CancellationToken, Task> handler) => SubscribeAsync(topic, (Delegate)handler);

    public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler) => SubscribeAsync(topic, (Delegate)handler);

    public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler) => SubscribeAsync(topic, (Delegate)handler);

    public void Unsubscribe(string topic, Delegate handler)
    {
        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            var newBag = new ConcurrentBag<Delegate>(handlers.Where(h => h != handler));
            _subscriptions.TryUpdate(topic, newBag, handlers);
        }
    }

    /// <summary>
    /// FIXED: Removed .Keys calls. Iterates directly over ConcurrentBags.
    /// </summary>
    public List<string> GetSubscriptionList(string topic)
    {
        var listeners = new List<string>();

        if (_subscriptions.TryGetValue(topic, out var handlers))
        {
            foreach (var handler in handlers)
            {
                listeners.Add($"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
            }
        }

        foreach (var global in _globalSubscribers)
        {
            listeners.Add($"[GLOBAL] {global.Method.DeclaringType?.Name}.{global.Method.Name}");
        }

        return listeners;
    }

   // ---------------------------------------------------------------------
    //                         SUBSCRIPTIONS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns a list of handlers for a strongly-typed topic.
    /// </summary>
    public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic)
    {
        return GetSubscriptionList(messageBusTopic.ToString());
    }

    // ---------------------------------------------------------------------
    //                         PUBLISHING
    // ---------------------------------------------------------------------

    /// <summary>
    /// Publishes a general status snapshot to the bus.
    /// </summary>
    public Task PublishStatusAsync(DeviceStatusMessage snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot == null) return Task.CompletedTask;
        
        // We use the common DeviceStatus string as the topic key
        var envelope = new MessageEnvelope("DeviceStatus", snapshot);
        return PublishAsync("DeviceStatus", envelope, cancellationToken);
    }

    /// <summary>
    /// Publishes a message using the strongly-typed MessageBusTopic.
    /// </summary>
    public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default)
    {
        // Bridges the typed topic to the core string-based PublishAsync
        return PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);
    }

    public List<string> GetActiveTopics() => _subscriptions.Keys.ToList();

    // ---------------------------------------------------------------------
    //                         PUBLISHING
    // ---------------------------------------------------------------------

    public async Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken ct = default)
    {
        _auditLogger.LogMessage(messageEnvelope.Payload, topic);
        await _messageChannel.Writer.WriteAsync(new MessageWorkItem(topic, messageEnvelope), ct);
        Interlocked.Increment(ref _messagesPublished);
    }

    public Task PublishStatusAsync(string topic, DeviceStatusMessage snapshot, CancellationToken ct)
    {
        if (snapshot == null) return Task.CompletedTask;
        var envelope = new MessageEnvelope(topic, snapshot);
        return PublishAsync(topic, envelope, ct);
    }

    public Task PublishStatusAsync(MessageBusTopic topic, IDeviceStatus status)
    {
        if (status == null) return Task.CompletedTask;
        var envelope = new MessageEnvelope(topic.ToString(), status);
        return PublishAsync(topic.ToString(), envelope);
    }

    public Task PublishStatusAsync(string keyDeviceName, IDeviceStatus status)
    {
        var envelope = new MessageEnvelope(keyDeviceName, status);
        return PublishAsync("DeviceStatus", envelope);
    }

    public Task PublishStatusAsync(DeviceKey keyDevice, IDeviceStatus status)
    {
        var envelope = new MessageEnvelope(keyDevice.DeviceName, status);
        return PublishAsync("DeviceStatus", envelope);
    }

    // ---------------------------------------------------------------------
    //                         INTERNAL DISPATCH
    // ---------------------------------------------------------------------

    private async Task ProcessMessagesAsync()
    {
        await foreach (var item in _messageChannel.Reader.ReadAllAsync())
        {
            try
            {
                await DispatchMessageInternal(item.Topic, item.Envelope);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _messagesFailed);
                _logger.LogError(ex, "Critical failure in dispatcher for topic {Topic}", item.Topic);
            }
        }
    }

    private async Task DispatchMessageInternal(string topic, MessageEnvelope envelope)
    {
        var handlers = GetHandlersForTopic(topic);

        // Process Topic Handlers
        foreach (var handler in handlers)
        {
            await ProcessWithHealthCheck(handler, topic, envelope);
        }

        // Process Global Handlers
        foreach (var global in _globalSubscribers)
        {
            await ProcessWithHealthCheck(global, topic, envelope);
        }
    }

    private async Task ProcessWithHealthCheck(Delegate handler, string topic, MessageEnvelope envelope)
    {
        var health = _handlerHealth.GetOrAdd(handler, _ => new SubscriberHealth());

        if (health.IsBroken)
        {
            await MoveToDeadLetterQueue(topic, envelope, $"Circuit Breaker Open for {handler.Method.Name}");
            return;
        }

        try
        {
            await ExecuteWithCircuitBreaker(handler, envelope, health);
        }
        catch (Exception ex)
        {
            await MoveToDeadLetterQueue(topic, envelope, ex.Message);
        }
    }

    private async Task ExecuteWithCircuitBreaker(Delegate handler, MessageEnvelope envelope, SubscriberHealth health)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            
            // Invoke the handler logic
            if (handler is Func<string, MessageEnvelope, Task> globalHandler)
            {
                await globalHandler("N/A", envelope); 
            }
            else if (handler is Func<MessageEnvelope, CancellationToken, Task> envelopeHandler)
            {
                await envelopeHandler(envelope, cts.Token);
            }
            else
            {
                var methodParams = handler.Method.GetParameters();
                object[] args = methodParams.Length switch
                {
                    1 => new object[] { envelope },
                    2 => new object[] { envelope, cts.Token },
                    _ => new object[] { envelope }
                };

                var result = handler.DynamicInvoke(args);
                if (result is Task task) await task.WaitAsync(cts.Token);
            }

            health.RecordSuccess();
        }
        catch (Exception ex)
        {
            health.RecordFailure(threshold: 3, timeout: TimeSpan.FromMinutes(1));
            _logger.LogError(ex, "Subscriber failed: {Method}", handler.Method.Name);
            throw; 
        }
    }

    private ConcurrentBag<Delegate> GetHandlersForTopic(string topic) => _subscriptions.GetOrAdd(topic, _ => new ConcurrentBag<Delegate>());

    private void LogSubscription(string topic, Delegate handler)
    {
        string handlerName = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
        _logger.LogInformation("Subscribed to '{Topic}' with {HandlerName}", topic, handlerName);
    }

    private async Task MoveToDeadLetterQueue(string topic, MessageEnvelope envelope, string reason)
    {
        var deadLetter = new { Timestamp = DateTime.Now, Topic = topic, Payload = envelope.Payload, Reason = reason };
        string fileName = $"DLQ_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():n}.json";
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DLQ", fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(deadLetter));
        _logger.LogWarning("DLQ: {Reason} -> {Path}", reason, fileName);
    }
}