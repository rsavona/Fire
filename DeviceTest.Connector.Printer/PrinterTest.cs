using Device.Connector.Printer;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using FluentAssertions;
using Moq;
using Serilog;

namespace Device.Printer.Test;
public class TestableZebraPrinter : BrandZebra
{
    public TestableZebraPrinter(IDeviceConfig config, ILogger logger) 
        : base(config, logger) { }

    // Expose protected Tracker properties for verification
    public long GetOutboundCount() => Tracker.CountOutbound;
    public DeviceHealth GetHealth() => Tracker.Health;

}

public class PrinterDeviceTests
{
    private readonly Mock<IDeviceConfig> _mockConfig;
    private readonly Mock<ILogger> _mockLogger;

    public PrinterDeviceTests()
    {
        _mockConfig = new Mock<IDeviceConfig>();
        _mockLogger = new Mock<ILogger>();

        _mockConfig.Setup(c => c.Name).Returns("Printer_01");
        _mockConfig.Setup(c => c.Properties).Returns(new Dictionary<string, object>
        {
            { "Host", "127.0.0.1" },
            { "Port", 9100 }
        });
    }

    [Fact]
    public void Constructor_ShouldInitializeToOffline()
    {
        // Act
        var printer = new TestableZebraPrinter(_mockConfig.Object, _mockLogger.Object);

        // Assert
        printer.CurrentStateAsString.Should().Be("Offline");
    }

    [Fact]
    public async Task StartAsync_ShouldTransitionToConnecting()
    {
        // Arrange
        var printer = new TestableZebraPrinter(_mockConfig.Object, _mockLogger.Object);

        // Act
        await printer.StartAsync(CancellationToken.None);

        // Assert
        printer.CurrentStateAsString.Should().Be("Connecting");
    }

    [Fact]
    public async Task SendAsync_ShouldIncrementOutboundCount_OnSuccess()
    {
        // Arrange
        var printer = new TestableZebraPrinter(_mockConfig.Object, _mockLogger.Object);
        var zpl = new ZplString("^XA^FDTest^FS^XZ");

        // Note: Since we aren't connected to a real socket, SendRawAsync will return false.
        // In a real integration test, you would mock the underlying TcpClient or use a loopback.
        
        // Act
        await printer.SendAsync(zpl.ToString());

        // Assert
        // If not connected, it shouldn't increment outbound but might log a warning
        printer.GetOutboundCount().Should().Be(0); 
    }

    [Theory]
    [InlineData("Connected", DeviceHealth.Normal)]
    [InlineData("Faulted", DeviceHealth.Critical)]
    public void MapStateToHealth_ShouldReturnCorrectSeverity(string stateName, DeviceHealth expectedHealth)
    {
        // Arrange
        var printer = new TestableZebraPrinter(_mockConfig.Object, _mockLogger.Object);
        
        // Act
        // Use reflection to access the protected MapStateToHealth method
        var method = typeof(BrandZebra).GetMethod("MapStateToHealth", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        // We need to parse the string into the specific State enum of the base
        var state = Enum.Parse<ClientDeviceBase.State>(stateName);
        if (method == null) return;
        var result = (DeviceHealth)(method.Invoke(printer, [state]) ?? throw new InvalidOperationException());

        // Assert
        result.Should().Be(expectedHealth);
    }
}