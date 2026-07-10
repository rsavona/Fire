using Fusion.Common;
using Fusion.Element.Link;

namespace Fusion.Element.Link.Tests;

public class LinkFrameQueueTests
{
    [Fact]
    public void SubscribeQueue_RoundTrips_TopicsAndQueueGroup()
    {
        var frame = new LinkFrame
        {
            Kind = LinkFrameKind.SubscribeQueue,
            Origin = "SITE_B",
            Topics = ["HOST_A.Inbound", "PRINTER.*"],
            QueueGroup = "WORKERS"
        };

        var parsed = LinkFrame.Parse(frame.ToWire().TrimEnd(LinkFrame.Terminator));

        Assert.NotNull(parsed);
        Assert.Equal(LinkFrameKind.SubscribeQueue, parsed!.Kind);
        Assert.Equal("SITE_B", parsed.Origin);
        Assert.Equal(["HOST_A.Inbound", "PRINTER.*"], parsed.Topics!);
        Assert.Equal("WORKERS", parsed.QueueGroup);
    }

    [Fact]
    public void UnsubscribeQueue_RoundTrips()
    {
        var frame = new LinkFrame
        {
            Kind = LinkFrameKind.UnsubscribeQueue,
            Origin = "SITE_B",
            Topics = ["HOST_A.Inbound"],
            QueueGroup = "WORKERS"
        };

        var parsed = LinkFrame.Parse(frame.ToWire().TrimEnd(LinkFrame.Terminator));

        Assert.NotNull(parsed);
        Assert.Equal(LinkFrameKind.UnsubscribeQueue, parsed!.Kind);
        Assert.Equal("WORKERS", parsed.QueueGroup);
    }

    [Fact]
    public void QueuePublish_RoundTrips_DeliveryId()
    {
        var correlation = Guid.NewGuid();
        var frame = new LinkFrame
        {
            Kind = LinkFrameKind.QueuePublish,
            Origin = "SITE_A",
            Topic = "HOST_A.INBOUND",
            Payload = "PRINT|BOX1",
            Gin = 42,
            HighPriority = false,
            SourceElement = "HOST_A",
            CorrelationId = correlation,
            DeliveryId = "d-123"
        };

        var parsed = LinkFrame.Parse(frame.ToWire().TrimEnd(LinkFrame.Terminator));

        Assert.NotNull(parsed);
        Assert.Equal(LinkFrameKind.QueuePublish, parsed!.Kind);
        Assert.Equal("HOST_A.INBOUND", parsed.Topic);
        Assert.Equal("PRINT|BOX1", parsed.Payload);
        Assert.Equal(42, parsed.Gin);
        Assert.False(parsed.HighPriority);
        Assert.Equal("HOST_A", parsed.SourceElement);
        Assert.Equal(correlation, parsed.CorrelationId);
        Assert.Equal("d-123", parsed.DeliveryId);
    }

    [Fact]
    public void Ack_RoundTrips_DeliveryId()
    {
        var frame = new LinkFrame
        {
            Kind = LinkFrameKind.Ack,
            Origin = "SITE_B",
            DeliveryId = "d-123"
        };

        var parsed = LinkFrame.Parse(frame.ToWire().TrimEnd(LinkFrame.Terminator));

        Assert.NotNull(parsed);
        Assert.Equal(LinkFrameKind.Ack, parsed!.Kind);
        Assert.Equal("d-123", parsed.DeliveryId);
    }

    [Fact]
    public void ToQueuePublishFrame_MapsEnvelopeAndDeliveryId()
    {
        var envelope = new MessageEnvelope("HOST_A.Inbound", "hello", gin: 7, client: "HOST_A");

        var frame = LinkConventions.ToQueuePublishFrame(envelope, "SITE_A", "d-9");

        Assert.Equal(LinkFrameKind.QueuePublish, frame.Kind);
        Assert.Equal("SITE_A", frame.Origin);
        Assert.Equal("HOST_A.INBOUND", frame.Topic);
        Assert.Equal("hello", frame.Payload);
        Assert.Equal(7, frame.Gin);
        Assert.Equal("d-9", frame.DeliveryId);
    }

    [Fact]
    public void QueuePublish_ToLocalEnvelope_CarriesLinkClientTag()
    {
        var frame = new LinkFrame
        {
            Kind = LinkFrameKind.QueuePublish,
            Origin = "SITE_A",
            Topic = "HOST_A.INBOUND",
            Payload = "hello",
            DeliveryId = "d-1"
        };

        var envelope = LinkConventions.ToLocalEnvelope(frame);

        Assert.Equal("LINK:SITE_A", envelope.Client);
        Assert.True(LinkConventions.IsFromLink(envelope));
    }
}
