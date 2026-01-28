using System.Diagnostics;
using Device.Plc.Suite;
using Device.Plc.Suite.Connector;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using FluentAssertions;
using Moq;
using Serilog;
using Xunit;

namespace DeviceTest.Connector.Plc;

public class PlcMultiClientDeviceTests : IDisposable
{
    private readonly Mock<IDeviceConfig> _mockConfig;
    private readonly Mock<ILogger> _mockLogger;
    private readonly CancellationTokenSource _cts;

    public PlcMultiClientDeviceTests()
    {
        _mockConfig = new Mock<IDeviceConfig>();
        _mockLogger = new Mock<ILogger>();
        _cts = new CancellationTokenSource();

        // Setup mandatory configuration properties
        _mockConfig.SetupGet(c => c.Name).Returns("PLC_UNIT_TEST");
        _mockConfig.SetupGet(c => c.Properties).Returns(new Dictionary<string, object>
        {
            { "DevicePort", 5000 },
            { "HeartbeatTimeoutMs", 1000 }
        });
    }

    [Fact]
    public void Constructor_ShouldInitializeToOfflineState()
    {
        // Act
        var device = new PlcServerDevice(_mockConfig.Object, _mockLogger.Object);

        // Assert
        device.CurrentStateAsString.Should().Be("Offline");
    }

    [Fact]
    public async Task StartAsync_ShouldTransitionToStarting()
    {
        // Arrange
        var device = new PlcServerDevice(_mockConfig.Object, _mockLogger.Object);

        // Act
        await device.StartAsync(_cts.Token);

        // Assert
        // It starts in 'Starting' before the TCP Listener reports it is actually 'Listening'
        device.CurrentStateAsString.Should().Be("Starting");
    }

    [Theory]
    [InlineData(PlcServerDevice.State.Connected, DeviceHealth.Normal)]
    [InlineData(PlcServerDevice.State.Listening, DeviceHealth.Normal)]
    [InlineData(PlcServerDevice.State.Faulted, DeviceHealth.Critical)]
    [InlineData(PlcServerDevice.State.Offline, DeviceHealth.Warning)]
    public void MapStateToHealth_ShouldAlignWithFireAmStandards(PlcServerDevice.State state, DeviceHealth expectedHealth)
    {
        // Arrange
        var device = new PlcServerDevice(_mockConfig.Object, _mockLogger.Object);
        
        // Use reflection to test the protected MapStateToHealth method
        var method = typeof(PlcServerDevice).GetMethod("MapStateToHealth", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        if (method == null) return;
        var result = (DeviceHealth)(method.Invoke(device, [state]) ?? throw new InvalidOperationException());

        // Assert
        result.Should().Be(expectedHealth);
    }

   
    [Fact]
    public async Task SendResponseAsync_ShouldLogWarning_WhenClientIsDisconnected()
    {
        // Arrange
        var device = new PlcServerDevice(_mockConfig.Object, _mockLogger.Object);
        string fakeClient = "non-existent-client";

        // Act
        await device.SendResponseAsync("{}", fakeClient);

        // Assert
        // Verify the logger was called with a warning about the disconnected client
        _mockLogger.Verify(x => x.Warning(It.Is<string>(s => s.Contains("disconnected client"))), Times.Once);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}