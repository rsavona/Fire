using Fusion.Common;
using Fusion.Common.Contracts;
using Fusion.Common.Messaging;
using Fusion.Core.Replay;

namespace Fusion.Core.Tests;

public class ReplayServiceTests
{
    /// <summary>Minimal capturing bus — records every publication, dispatches nothing.</summary>
    private sealed class FakeBus : IMessageBus
    {
        public readonly List<(string Topic, MessageEnvelope Envelope)> Published = new();

        public Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
        {
            lock (Published) Published.Add((topic, messageEnvelope));
            return Task.CompletedTask;
        }

        public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
            => PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);

        public Task<bool> SubscribeAsync(string topic, Delegate handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync(string topic, Func<MessageEnvelope, CancellationToken, Task> handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler) => Task.FromResult(true);
        public void Unsubscribe(string topic, Delegate handler) { }
        public Task PublishStatusAsync(IElementStatus snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public List<string> GetActiveTopics() => new();
        public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic) => new();
        public List<string> GetSubscriptionList(string messageBusTopic) => new();
    }

    private static AuditRecord Record(string topic, int secondOffset, string payload = "payload") => new()
    {
        Timestamp = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero).AddSeconds(secondOffset),
        Topic = topic,
        PayloadType = "String",
        PayloadText = payload
    };

    /// <summary>Virtualized clock: collects requested delays instead of sleeping.</summary>
    private static (List<TimeSpan> Delays, Func<TimeSpan, CancellationToken, Task> Delay) VirtualClock()
    {
        var delays = new List<TimeSpan>();
        return (delays, (span, _) => { delays.Add(span); return Task.CompletedTask; });
    }

    [Fact]
    public async Task RunAsync_PublishesOnlyOnReplayNamespacedTopics()
    {
        var bus = new FakeBus();
        var (_, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        var records = new[]
        {
            Record("HOST_A.MsgReceived", 0),
            Record("System.DataFlow", 1),
            Record("All_Elements.StatusMessage", 2)
        };

        await service.RunAsync(records, speed: 0);

        Assert.Equal(3, bus.Published.Count);
        Assert.All(bus.Published, p =>
            Assert.StartsWith(ReplayService.TopicPrefix, p.Topic, StringComparison.Ordinal));

        // The original topics must never be published to.
        foreach (var original in records.Select(r => r.Topic))
        {
            Assert.DoesNotContain(bus.Published, p =>
                string.Equals(p.Topic, original, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal("REPLAY.HOST_A.MsgReceived", bus.Published[0].Topic);
        Assert.Equal("REPLAY.System.DataFlow", bus.Published[1].Topic);
    }

    [Fact]
    public async Task RunAsync_SkipsRecordsAlreadyOnReplayTopics()
    {
        var bus = new FakeBus();
        var (_, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        await service.RunAsync(new[]
        {
            Record("REPLAY.HOST_A.MsgReceived", 0),
            Record("HOST_A.MsgReceived", 1)
        }, speed: 0);

        var publication = Assert.Single(bus.Published);
        Assert.Equal("REPLAY.HOST_A.MsgReceived", publication.Topic);
        // Not double-prefixed.
        Assert.DoesNotContain("REPLAY.REPLAY", publication.Topic);
    }

    [Fact]
    public async Task RunAsync_Speed1x_ReproducesRecordedGaps()
    {
        var bus = new FakeBus();
        var (delays, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        await service.RunAsync(new[]
        {
            Record("A.Msg", 0),
            Record("A.Msg", 2),
            Record("A.Msg", 5)
        }, speed: 1);

        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, delays);
    }

    [Fact]
    public async Task RunAsync_Speed5x_DividesGapsByFive()
    {
        var bus = new FakeBus();
        var (delays, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        await service.RunAsync(new[]
        {
            Record("A.Msg", 0),
            Record("A.Msg", 10)
        }, speed: 5);

        var gap = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), gap);
    }

    [Fact]
    public async Task RunAsync_MaxSpeed_NeverDelays()
    {
        var bus = new FakeBus();
        var (delays, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        await service.RunAsync(new[]
        {
            Record("A.Msg", 0),
            Record("A.Msg", 30),
            Record("A.Msg", 90)
        }, speed: 0);

        Assert.Empty(delays);
        Assert.Equal(3, bus.Published.Count);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressAndCompletion()
    {
        var bus = new FakeBus();
        var (_, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);
        var states = new List<ReplayState>();
        service.ProgressChanged += p => states.Add(p.State);

        await service.RunAsync(new[] { Record("A.Msg", 0), Record("A.Msg", 1) }, speed: 0);

        Assert.Equal(ReplayState.Completed, service.Progress.State);
        Assert.Equal(2, service.Progress.Published);
        Assert.Contains(ReplayState.Playing, states);
        Assert.Equal(ReplayState.Completed, states.Last());
    }

    [Fact]
    public async Task RunAsync_TypedFlowEventPayload_IsRehydrated()
    {
        var bus = new FakeBus();
        var (_, delay) = VirtualClock();
        var service = new ReplayService(bus, delay);

        AuditLogReader.TryParseLine(
            """{"@t":"2026-07-10T08:00:01.5000000Z","@mt":"[{Topic,-40}] [{PayloadType,-15}]  {@Payload}","Topic":"System.DataFlow","PayloadType":"FlowEvent","Payload":{"Source":"HOST_A","Force":"EchoReaction","Destination":"LINK_SERVER"},"AuditLog":true}""",
            out var record);

        await service.RunAsync(new[] { record }, speed: 0);

        var publication = Assert.Single(bus.Published);
        Assert.Equal("REPLAY.System.DataFlow", publication.Topic);
        var flow = Assert.IsType<FlowEvent>(publication.Envelope.Payload);
        Assert.Equal("HOST_A", flow.Source);
        Assert.Equal("LINK_SERVER", flow.Destination);
        Assert.Equal(record.Topic, publication.Envelope.Header.Metadata["ReplayOriginalTopic"]);
    }

    [Fact]
    public async Task RunAsync_Cancellation_StopsPublishing()
    {
        var bus = new FakeBus();
        using var cts = new CancellationTokenSource();
        var service = new ReplayService(bus, (_, _) => Task.CompletedTask);

        var records = Enumerable.Range(0, 100).Select(i => Record("A.Msg", i));
        int cancelAfter = 5;
        service.ProgressChanged += p =>
        {
            if (p.Published >= cancelAfter) cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(records, speed: 0, cts.Token));

        Assert.True(bus.Published.Count < 100);
    }
}
