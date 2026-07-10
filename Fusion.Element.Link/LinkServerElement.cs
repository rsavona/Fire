using System.Collections.Concurrent;
using System.Timers;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.TCP_Classes;
using Fusion.Common.TcpSocket;
using Serilog.Core;

namespace Fusion.Element.Link;

/// <summary>
/// Listens for LinkClientElement connections from other Fusion instances.
/// Each connected client can subscribe to local bus topics (wildcards supported);
/// matching envelopes are forwarded to that client. Publish frames received from
/// a client are republished onto the local bus.
/// </summary>
public class LinkServerElement : TcpServerElementBase<SocketMessageProcessor>
{
    private readonly string _origin;

    // clientKey -> (topic pattern -> bus handler), so subscriptions can be
    // removed exactly when that client disconnects.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Func<MessageEnvelope, CancellationToken, Task>>>
        _clientSubscriptions = new();

    // Competing-consumer dispatch. One bus handler per (topic pattern, queue group),
    // removed only when the last member leaves the group.
    private readonly QueueDispatcher _queueDispatcher;
    private readonly ConcurrentDictionary<string, Func<MessageEnvelope, CancellationToken, Task>>
        _queueGroupHandlers = new(StringComparer.OrdinalIgnoreCase);

    public LinkServerElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger,
               new SocketMessageProcessor(config.Name, logger),
               swtch,
               GetPort(config),
               new SequenceTerminationStrategy([(byte)LinkFrame.Terminator]),
               GetMaxClients(config))
    {
        _origin = LinkConventions.ResolveOrigin(config);

        _queueDispatcher = new QueueDispatcher(
            send: (clientKey, frame, ct) => SendAsync(clientKey, frame.ToWire(), ct),
            origin: _origin,
            ackTimeoutMs: GetIntProperty(config, "AckTimeoutMs", 30000),
            redeliverAttempts: GetIntProperty(config, "RedeliverAttempts", 3),
            isMemberHealthy: IsClientFresh,
            logger: logger.GetRawLogger());
        _queueDispatcher.DeliveryExhausted += OnQueueDeliveryExhaustedAsync;

        Processor.MessageReceived += OnFrameReceivedAsync;
    }

    /// <summary>
    /// Health signal for queue member selection: a member is skipped when it has
    /// been silent (no frames, no heartbeats) longer than the heartbeat timeout.
    /// </summary>
    private bool IsClientFresh(string clientKey)
    {
        if (HeartbeatTimeoutMs <= 0) return true;
        return !ConnectedClients.TryGetValue(clientKey, out var lastSeen) ||
               (DateTime.UtcNow - lastSeen).TotalMilliseconds <= HeartbeatTimeoutMs;
    }

    private async Task OnFrameReceivedAsync(object message)
    {
        if (message is not MessageEnvelope inbound) return;

        string clientKey = inbound.Client;
        var frame = LinkFrame.Parse(inbound.Payload?.ToString() ?? string.Empty);
        if (frame == null)
        {
            Logger.Warning("[{Dev}] Discarded unparseable link frame from {Client}", Config.Name, clientKey);
            return;
        }

        UpdateClientTimestamp(clientKey);

        try
        {
            switch (frame.Kind)
            {
                case LinkFrameKind.Subscribe:
                    await Machine.FireAsync(Event.MessageReceived);
                    foreach (string topic in frame.Topics ?? [])
                    {
                        AddClientSubscription(clientKey, frame.Origin, topic);
                    }
                    break;

                case LinkFrameKind.Unsubscribe:
                    await Machine.FireAsync(Event.MessageReceived);
                    foreach (string topic in frame.Topics ?? [])
                    {
                        RemoveClientSubscription(clientKey, topic);
                    }
                    break;

                case LinkFrameKind.Publish when !string.IsNullOrEmpty(frame.Topic):
                    await Machine.FireAsync(Event.MessageReceived);
                    var envelope = LinkConventions.ToLocalEnvelope(frame);
                    Logger.Verbose("[{Dev}] Remote publish from {Origin}: {Topic}", Config.Name, frame.Origin, frame.Topic);
                    await MessageBus.PublishAsync(envelope.Destination.ToString(), envelope);
                    break;

                case LinkFrameKind.SubscribeQueue when !string.IsNullOrWhiteSpace(frame.QueueGroup):
                    await Machine.FireAsync(Event.MessageReceived);
                    foreach (string topic in frame.Topics ?? [])
                    {
                        AddQueueMember(clientKey, frame.Origin, topic, frame.QueueGroup!);
                    }
                    break;

                case LinkFrameKind.UnsubscribeQueue when !string.IsNullOrWhiteSpace(frame.QueueGroup):
                    await Machine.FireAsync(Event.MessageReceived);
                    foreach (string topic in frame.Topics ?? [])
                    {
                        await RemoveQueueMemberAsync(clientKey, topic, frame.QueueGroup!);
                    }
                    break;

                case LinkFrameKind.Ack:
                    if (!_queueDispatcher.Acknowledge(frame.DeliveryId))
                    {
                        Logger.Debug("[{Dev}] Late/unknown ack {DeliveryId} from {Client}",
                            Config.Name, frame.DeliveryId, clientKey);
                    }
                    break;

                case LinkFrameKind.Heartbeat:
                    await SendAsync(clientKey,
                        new LinkFrame { Kind = LinkFrameKind.HeartbeatAck, Origin = _origin }.ToWire());
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error handling {Kind} frame from {Client}", Config.Name, frame.Kind, clientKey);
            OnError("LinkFrameHandling", ex);
        }
    }

    private void AddClientSubscription(string clientKey, string remoteOrigin, string topic)
    {
        var subscriptions = _clientSubscriptions.GetOrAdd(clientKey,
            _ => new ConcurrentDictionary<string, Func<MessageEnvelope, CancellationToken, Task>>(StringComparer.OrdinalIgnoreCase));

        Func<MessageEnvelope, CancellationToken, Task> handler = async (envelope, ct) =>
        {
            // Loop guard: never forward an envelope that itself arrived over a link.
            if (LinkConventions.IsFromLink(envelope)) return;
            await SendAsync(clientKey, LinkConventions.ToPublishFrame(envelope, _origin).ToWire(), ct);
        };

        if (!subscriptions.TryAdd(topic, handler))
        {
            Logger.Debug("[{Dev}] {Client} already subscribed to {Topic}", Config.Name, clientKey, topic);
            return;
        }

        MessageBus.SubscribeAsync(topic, handler);
        Logger.Information("[{Dev}] Remote instance {Origin} ({Client}) subscribed to local topic {Topic}",
            Config.Name, remoteOrigin, clientKey, topic);
    }

    private void RemoveClientSubscription(string clientKey, string topic)
    {
        if (_clientSubscriptions.TryGetValue(clientKey, out var subscriptions) &&
            subscriptions.TryRemove(topic, out var handler))
        {
            MessageBus.Unsubscribe(topic, handler);
            Logger.Information("[{Dev}] {Client} unsubscribed from {Topic}", Config.Name, clientKey, topic);
        }
    }

    private void AddQueueMember(string clientKey, string remoteOrigin, string topic, string queueGroup)
    {
        bool groupCreated = _queueDispatcher.AddMember(topic, queueGroup, clientKey);
        if (groupCreated)
        {
            Func<MessageEnvelope, CancellationToken, Task> handler =
                (envelope, ct) => _queueDispatcher.DispatchAsync(topic, queueGroup, envelope, ct);

            _queueGroupHandlers[QueueHandlerKey(topic, queueGroup)] = handler;
            MessageBus.SubscribeAsync(topic, handler);
        }

        Logger.Information("[{Dev}] Remote instance {Origin} ({Client}) joined queue group {Group} on {Topic}",
            Config.Name, remoteOrigin, clientKey, queueGroup, topic);
    }

    private async Task RemoveQueueMemberAsync(string clientKey, string topic, string queueGroup)
    {
        bool groupEmptied = await _queueDispatcher.RemoveMemberAsync(topic, queueGroup, clientKey);
        if (groupEmptied)
        {
            RemoveQueueGroupSubscription(topic, queueGroup);
        }

        Logger.Information("[{Dev}] {Client} left queue group {Group} on {Topic}",
            Config.Name, clientKey, queueGroup, topic);
    }

    private void RemoveQueueGroupSubscription(string topic, string queueGroup)
    {
        if (_queueGroupHandlers.TryRemove(QueueHandlerKey(topic, queueGroup), out var handler))
        {
            MessageBus.Unsubscribe(topic, handler);
            Logger.Information("[{Dev}] Queue group {Group} on {Topic} is empty; bus subscription removed",
                Config.Name, queueGroup, topic);
        }
    }

    private static string QueueHandlerKey(string topic, string queueGroup) => $"{topic}|{queueGroup}";

    private async Task OnQueueDeliveryExhaustedAsync(MessageEnvelope envelope, string reason)
    {
        Logger.Warning("[{Dev}] Undeliverable queue envelope for {Topic}: {Reason}",
            Config.Name, envelope.Destination, reason);

        await MessageBus.PublishAsync(MessageBusTopic.InternalError.ToString(),
            new MessageEnvelope(MessageBusTopic.InternalError, new BusErrorMessage
            {
                OriginalTopic = envelope.Destination.ToString(),
                ExceptionMessage = $"Queue delivery abandoned: {reason}",
                OriginalPayload = envelope.Payload
            }, client: Config.Name));
    }

    protected override void OnWatchdogScan(object? sender, ElapsedEventArgs e)
    {
        base.OnWatchdogScan(sender, e);
        _ = _queueDispatcher.ScanTimeoutsAsync(DateTime.UtcNow);
    }

    protected override void OnClientDisconnected(string client, int remaining)
    {
        if (_clientSubscriptions.TryRemove(client, out var subscriptions))
        {
            foreach (var (topic, handler) in subscriptions)
            {
                MessageBus.Unsubscribe(topic, handler);
            }

            Logger.Information("[{Dev}] Cleaned up {Count} bus subscription(s) for disconnected client {Client}",
                Config.Name, subscriptions.Count, client);
        }

        // Redeliver the departed member's pending queue deliveries and drop
        // bus subscriptions for any groups that are now empty.
        _ = Task.Run(async () =>
        {
            var emptiedGroups = await _queueDispatcher.RemoveClientAsync(client);
            foreach (var (topic, queueGroup) in emptiedGroups)
            {
                RemoveQueueGroupSubscription(topic, queueGroup);
            }
        });
    }

    private static int GetPort(IElementBlueprint config) =>
        config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;

    private static int GetMaxClients(IElementBlueprint config) =>
        config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 4;

    private static int GetIntProperty(IElementBlueprint config, string key, int fallback) =>
        config.Properties.TryGetValue(key, out var v) ? Convert.ToInt32(v) : fallback;
}
