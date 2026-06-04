using Fusion.Common;
using Xunit;

namespace Fusion.Common.Tests;

public class MessageHeaderTests
{
    [Fact]
    public void MessageEnvelope_Constructor_InitializesHeaderWithClient()
    {
        // Arrange
        var topic = new MessageBusTopic("TEST");
        var payload = "Payload";
        var client = "TestClient";

        // Act
        var envelope = new MessageEnvelope(topic, payload, client: client);

        // Assert
        Assert.NotNull(envelope.Header);
        Assert.Equal(client, envelope.Header.Source);
        Assert.NotEqual(Guid.Empty, envelope.Header.MessageId);
        Assert.NotEqual(Guid.Empty, envelope.Header.CorrelationId);
    }

    [Fact]
    public void MessageEnvelope_Constructor_UsesProvidedHeader()
    {
        // Arrange
        var topic = new MessageBusTopic("TEST");
        var payload = "Payload";
        var customHeader = new MessageHeader { Source = "CustomSource", CorrelationId = Guid.NewGuid() };

        // Act
        var envelope = new MessageEnvelope(topic, payload, header: customHeader);

        // Assert
        Assert.Equal(customHeader, envelope.Header);
        Assert.Equal("CustomSource", envelope.Header.Source);
    }
}
