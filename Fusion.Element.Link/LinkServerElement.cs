using System.Collections.Concurrent;
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

    public LinkServerElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger,
               new SocketMessageProcessor(config.Name, logger),
               swtch,
               GetPort(config),
               new SequenceTerminationStrategy([(byte)LinkFrame.Terminator]),
               GetMaxClients(config))
    {
        _origin = LinkConventions.ResolveOrigin(config);
        Processor.MessageReceived += OnFrameReceivedAsync;
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
    }

    private static int GetPort(IElementBlueprint config) =>
        config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 0;

    private static int GetMaxClients(IElementBlueprint config) =>
        config.Properties.TryGetValue("MaxClients", out var c) ? Convert.ToInt32(c) : 4;
}
