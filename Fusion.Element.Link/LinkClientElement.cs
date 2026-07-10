using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.TCP_Classes;
using Serilog.Core;

namespace Fusion.Element.Link;

/// <summary>
/// Connects to a LinkServerElement in another Fusion instance. On connect it sends
/// its configured RemoteTopics subscription list; every Publish frame received back
/// is republished onto the local message bus, so local elements can subscribe to
/// remote topics as if they were local.
/// </summary>
public class LinkClientElement : TcpClientElementBase, IMessageProvider
{
    public event Func<object, object, Task>? MessageReceived;

    private readonly ITerminationStrategy _frameTermination =
        new SequenceTerminationStrategy([(byte)LinkFrame.Terminator]);

    private readonly string _origin;

    /// <summary>Remote topic patterns this instance pulls from the server.</summary>
    public IReadOnlyList<string> RemoteTopics { get; }

    /// <summary>Local topic patterns this instance pushes to the server.</summary>
    public IReadOnlyList<string> PublishTopics { get; }

    /// <summary>Remote topic patterns this instance consumes as a queue-group member.</summary>
    public IReadOnlyList<string> QueueTopics { get; }

    /// <summary>Competing-consumer group this instance joins for QueueTopics.</summary>
    public string QueueGroup { get; }

    public LinkClientElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, needsHb: true)
    {
        _origin = LinkConventions.ResolveOrigin(config);
        RemoteTopics = LinkConventions.ParseTopicList(config, "RemoteTopics");
        PublishTopics = LinkConventions.ParseTopicList(config, "PublishTopics");
        QueueTopics = LinkConventions.ParseTopicList(config, "QueueTopics");

        QueueGroup = config.Properties.TryGetValue("QueueGroup", out var group) &&
                     !string.IsNullOrWhiteSpace(group?.ToString())
            ? group.ToString()!
            : _origin;
    }

    protected override ITerminationStrategy ReceiveTerminationStrategy => _frameTermination;

    protected override async Task ElementConnectedAsync()
    {
        await base.ElementConnectedAsync();

        if (RemoteTopics.Count > 0)
        {
            var subscribe = new LinkFrame
            {
                Kind = LinkFrameKind.Subscribe,
                Origin = _origin,
                Topics = RemoteTopics.ToList()
            };

            await SendAsync(subscribe.ToWire(), CancellationToken.None);
            Logger.Information("[{Dev}] Link established. Subscribed to {Count} remote topic(s): {Topics}",
                Config.Name, RemoteTopics.Count, string.Join(", ", RemoteTopics));
        }

        if (QueueTopics.Count > 0)
        {
            var subscribeQueue = new LinkFrame
            {
                Kind = LinkFrameKind.SubscribeQueue,
                Origin = _origin,
                Topics = QueueTopics.ToList(),
                QueueGroup = QueueGroup
            };

            await SendAsync(subscribeQueue.ToWire(), CancellationToken.None);
            Logger.Information("[{Dev}] Joined queue group {Group} for {Count} topic(s): {Topics}",
                Config.Name, QueueGroup, QueueTopics.Count, string.Join(", ", QueueTopics));
        }
    }

    protected override async Task HandleReceivedDataAsync(string incomingData)
    {
        var frame = LinkFrame.Parse(incomingData);
        if (frame == null)
        {
            Logger.Warning("[{Dev}] Discarded unparseable link frame: {Raw}", Config.Name, incomingData);
            return;
        }

        switch (frame.Kind)
        {
            case LinkFrameKind.Publish when !string.IsNullOrEmpty(frame.Topic):
                var envelope = LinkConventions.ToLocalEnvelope(frame);
                Logger.Verbose("[{Dev}] Remote publish from {Origin}: {Topic}", Config.Name, frame.Origin, frame.Topic);
                if (MessageReceived != null)
                {
                    await MessageReceived.Invoke(this, envelope);
                }
                break;

            case LinkFrameKind.QueuePublish when !string.IsNullOrEmpty(frame.Topic):
                await HandleQueuePublishAsync(frame);
                break;

            case LinkFrameKind.HeartbeatAck:
                // Normally consumed by IsHeartbeat before reaching here.
                break;

            default:
                Logger.Debug("[{Dev}] Ignored link frame of kind {Kind}", Config.Name, frame.Kind);
                break;
        }
    }

    /// <summary>
    /// Republishes a queue delivery locally, then acks it. The Ack is sent only
    /// AFTER the local publish succeeds — if it throws, the server redelivers
    /// (at-least-once; consumers needing exactly-once must dedupe on DeliveryId).
    /// </summary>
    private async Task HandleQueuePublishAsync(LinkFrame frame)
    {
        var envelope = LinkConventions.ToLocalEnvelope(frame);
        Logger.Verbose("[{Dev}] Queue delivery {DeliveryId} from {Origin}: {Topic}",
            Config.Name, frame.DeliveryId, frame.Origin, frame.Topic);

        try
        {
            if (MessageReceived != null)
            {
                await MessageReceived.Invoke(this, envelope);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Local publish of queue delivery {DeliveryId} failed; withholding ack",
                Config.Name, frame.DeliveryId);
            return;
        }

        await SendAsync(new LinkFrame
        {
            Kind = LinkFrameKind.Ack,
            Origin = _origin,
            DeliveryId = frame.DeliveryId
        }.ToWire(), CancellationToken.None);
    }

    /// <summary>
    /// Forwards a local bus envelope to the remote instance as a Publish frame.
    /// </summary>
    public Task SendPublishAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        return SendAsync(LinkConventions.ToPublishFrame(envelope, _origin).ToWire(), ct);
    }

    protected override string GetHeartbeatMessage() =>
        new LinkFrame { Kind = LinkFrameKind.Heartbeat, Origin = _origin }.ToWire();

    protected override bool IsHeartbeat(string incomingData) =>
        incomingData.Contains("\"Kind\":\"HeartbeatAck\"", StringComparison.OrdinalIgnoreCase);
}
