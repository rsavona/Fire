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

    public LinkClientElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(bus, config, logger, swtch, needsHb: true)
    {
        _origin = LinkConventions.ResolveOrigin(config);
        RemoteTopics = LinkConventions.ParseTopicList(config, "RemoteTopics");
        PublishTopics = LinkConventions.ParseTopicList(config, "PublishTopics");
    }

    protected override ITerminationStrategy ReceiveTerminationStrategy => _frameTermination;

    protected override async Task ElementConnectedAsync()
    {
        await base.ElementConnectedAsync();

        if (RemoteTopics.Count == 0) return;

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

            case LinkFrameKind.HeartbeatAck:
                // Normally consumed by IsHeartbeat before reaching here.
                break;

            default:
                Logger.Debug("[{Dev}] Ignored link frame of kind {Kind}", Config.Name, frame.Kind);
                break;
        }
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
