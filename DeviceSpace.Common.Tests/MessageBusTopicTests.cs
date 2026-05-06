using DeviceSpace.Common;
using Xunit;

namespace DeviceSpace.Common.Tests;

public class MessageBusTopicTests
{
    [Theory]
    [InlineData("TURA_DB", "TURA_DB", "DEFAULT", "")]
    [InlineData("TURA_DB.Log", "TURA_DB", "LOG", "")]
    [InlineData("TURA_DB.Log.Extra", "TURA_DB", "LOG", "EXTRA")]
    [InlineData("TURA_DB.Log.Extra.More", "TURA_DB", "LOG", "EXTRA.MORE")]
    [InlineData(".", "", "", "")]
    public void Constructor_WithStrTopic_ParsesCorrectly(string topic, string expectedDevice, string expectedType, string expectedDiscriminator)
    {
        // Act
        var result = new MessageBusTopic(topic);

        // Assert
        Assert.Equal(expectedDevice, result.DeviceName);
        Assert.Equal(expectedType, result.MessageType);
        Assert.Equal(expectedDiscriminator, result.Discriminator);
    }

    [Fact]
    public void Constructor_WithEmptyTopic_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new MessageBusTopic(""));
    }
}
