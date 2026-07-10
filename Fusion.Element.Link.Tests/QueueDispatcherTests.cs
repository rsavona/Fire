using Fusion.Common;
using Fusion.Element.Link;

namespace Fusion.Element.Link.Tests;

public class QueueDispatcherTests
{
    private const string Topic = "HOST_A.INBOUND";
    private const string Group = "WORKERS";

    private static MessageEnvelope Envelope(string payload = "work") =>
        new(Topic, payload, client: "HOST_A");

    /// <summary>Records every (member, frame) the dispatcher tries to send.</summary>
    private sealed class SendRecorder
    {
        public readonly List<(string Member, LinkFrame Frame)> Sends = [];
        public Func<string, bool> Accept = _ => true;

        public Task<bool> SendAsync(string member, LinkFrame frame, CancellationToken ct)
        {
            Sends.Add((member, frame));
            return Task.FromResult(Accept(member));
        }
    }

    private static (QueueDispatcher Dispatcher, SendRecorder Recorder) Create(
        int ackTimeoutMs = 30000, int redeliverAttempts = 3, Func<string, bool>? isHealthy = null)
    {
        var recorder = new SendRecorder();
        var dispatcher = new QueueDispatcher(recorder.SendAsync, "SITE_A",
            ackTimeoutMs, redeliverAttempts, isHealthy);
        return (dispatcher, recorder);
    }

    [Fact]
    public void AddMember_FirstJoinCreatesGroup_LaterJoinsDoNot()
    {
        var (dispatcher, _) = Create();

        Assert.True(dispatcher.AddMember(Topic, Group, "A"));
        Assert.False(dispatcher.AddMember(Topic, Group, "B"));
        Assert.False(dispatcher.AddMember(Topic, Group, "B")); // duplicate join is a no-op
        Assert.True(dispatcher.AddMember(Topic, "OTHER_GROUP", "A")); // distinct group
    }

    [Fact]
    public async Task RemoveMember_LastLeaveEmptiesGroup()
    {
        var (dispatcher, _) = Create();
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        Assert.False(await dispatcher.RemoveMemberAsync(Topic, Group, "A"));
        Assert.True(await dispatcher.RemoveMemberAsync(Topic, Group, "B"));
    }

    [Fact]
    public async Task Dispatch_RoundRobinsAcrossMembers()
    {
        var (dispatcher, recorder) = Create();
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        for (int i = 0; i < 4; i++)
        {
            await dispatcher.DispatchAsync(Topic, Group, Envelope($"m{i}"));
        }

        Assert.Equal(["A", "B", "A", "B"], recorder.Sends.Select(s => s.Member));
        Assert.All(recorder.Sends, s => Assert.Equal(LinkFrameKind.QueuePublish, s.Frame.Kind));
        Assert.All(recorder.Sends, s => Assert.False(string.IsNullOrEmpty(s.Frame.DeliveryId)));
        Assert.Equal(4, dispatcher.PendingCount);
    }

    [Fact]
    public async Task Ack_ClearsPending()
    {
        var (dispatcher, recorder) = Create();
        dispatcher.AddMember(Topic, Group, "A");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        string deliveryId = recorder.Sends.Single().Frame.DeliveryId!;

        Assert.Equal(1, dispatcher.PendingCount);
        Assert.True(dispatcher.Acknowledge(deliveryId));
        Assert.Equal(0, dispatcher.PendingCount);

        Assert.False(dispatcher.Acknowledge(deliveryId)); // duplicate ack
        Assert.False(dispatcher.Acknowledge("unknown"));
        Assert.False(dispatcher.Acknowledge(null));
    }

    [Fact]
    public async Task AckedDelivery_IsNotRedeliveredByTimeoutScan()
    {
        var (dispatcher, recorder) = Create(ackTimeoutMs: 0);
        dispatcher.AddMember(Topic, Group, "A");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        dispatcher.Acknowledge(recorder.Sends.Single().Frame.DeliveryId);

        await dispatcher.ScanTimeoutsAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.Single(recorder.Sends);
    }

    [Fact]
    public async Task Timeout_RedeliversToNextMember()
    {
        var (dispatcher, recorder) = Create(ackTimeoutMs: 0);
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        Assert.Equal("A", recorder.Sends.Single().Member);

        await dispatcher.ScanTimeoutsAsync(DateTime.UtcNow.AddMilliseconds(1));

        Assert.Equal(2, recorder.Sends.Count);
        Assert.Equal("B", recorder.Sends[1].Member);
        Assert.Equal(recorder.Sends[0].Frame.DeliveryId, recorder.Sends[1].Frame.DeliveryId);
        Assert.Equal(1, dispatcher.PendingCount); // still awaiting an ack
    }

    [Fact]
    public async Task Disconnect_RedeliversPendingImmediately_AndReportsEmptiedGroups()
    {
        var (dispatcher, recorder) = Create();
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        Assert.Equal("A", recorder.Sends.Single().Member);

        var emptied = await dispatcher.RemoveClientAsync("A");

        Assert.Empty(emptied); // B still holds the group
        Assert.Equal(2, recorder.Sends.Count);
        Assert.Equal("B", recorder.Sends[1].Member);

        emptied = await dispatcher.RemoveClientAsync("B");
        Assert.Equal([(Topic, Group)], emptied);
    }

    [Fact]
    public async Task Exhaustion_FiresDeliveryExhausted_WithOriginalEnvelope()
    {
        var (dispatcher, _) = Create(ackTimeoutMs: 0, redeliverAttempts: 1);
        dispatcher.AddMember(Topic, Group, "A");

        var exhausted = new List<(MessageEnvelope Envelope, string Reason)>();
        dispatcher.DeliveryExhausted += (env, reason) =>
        {
            exhausted.Add((env, reason));
            return Task.CompletedTask;
        };

        var envelope = Envelope("doomed");
        await dispatcher.DispatchAsync(Topic, Group, envelope);
        await dispatcher.ScanTimeoutsAsync(DateTime.UtcNow.AddMinutes(1)); // redelivery 1
        await dispatcher.ScanTimeoutsAsync(DateTime.UtcNow.AddMinutes(2)); // exhausted

        var only = Assert.Single(exhausted);
        Assert.Same(envelope, only.Envelope);
        Assert.Contains("exhausted", only.Reason);
        Assert.Equal(0, dispatcher.PendingCount);

        // No further redelivery after exhaustion.
        await dispatcher.ScanTimeoutsAsync(DateTime.UtcNow.AddMinutes(3));
        Assert.Single(exhausted);
    }

    [Fact]
    public async Task Disconnect_WithNoRemainingMembers_ExhaustsPending()
    {
        var (dispatcher, _) = Create();
        dispatcher.AddMember(Topic, Group, "A");

        var exhausted = new List<string>();
        dispatcher.DeliveryExhausted += (_, reason) =>
        {
            exhausted.Add(reason);
            return Task.CompletedTask;
        };

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        await dispatcher.RemoveClientAsync("A");

        var reason = Assert.Single(exhausted);
        Assert.Contains("no members remain", reason);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task LinkTaggedEnvelopes_AreNeverQueueDispatched()
    {
        var (dispatcher, recorder) = Create();
        dispatcher.AddMember(Topic, Group, "A");

        var fromLink = new MessageEnvelope(Topic, "work", client: "LINK:SITE_B");
        await dispatcher.DispatchAsync(Topic, Group, fromLink);

        Assert.Empty(recorder.Sends);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task Dispatch_ToUnknownGroup_IsIgnored()
    {
        var (dispatcher, recorder) = Create();

        await dispatcher.DispatchAsync(Topic, Group, Envelope());

        Assert.Empty(recorder.Sends);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task UnhealthyMembers_AreSkipped()
    {
        var (dispatcher, recorder) = Create(isHealthy: m => m == "B");
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());
        await dispatcher.DispatchAsync(Topic, Group, Envelope());

        Assert.Equal(["B", "B"], recorder.Sends.Select(s => s.Member));
    }

    [Fact]
    public async Task AllMembersUnhealthy_FallsBackToRoundRobin()
    {
        var (dispatcher, recorder) = Create(isHealthy: _ => false);
        dispatcher.AddMember(Topic, Group, "A");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());

        Assert.Equal("A", recorder.Sends.Single().Member);
    }

    [Fact]
    public async Task FailedSend_RedeliversImmediatelyToNextMember()
    {
        var (dispatcher, recorder) = Create();
        recorder.Accept = m => m != "A";
        dispatcher.AddMember(Topic, Group, "A");
        dispatcher.AddMember(Topic, Group, "B");

        await dispatcher.DispatchAsync(Topic, Group, Envelope());

        Assert.Equal(["A", "B"], recorder.Sends.Select(s => s.Member));
        Assert.Equal(1, dispatcher.PendingCount);
    }
}
