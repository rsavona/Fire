using Device.Connector.ActiveMQ;
using DeviceSpace.Common.BaseClasses;
using DeviceTest.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;


namespace DeviceTest.Connector.ActiveMq;

public partial class ActiveMqManagerTests : DeviceManagerTestBase<ActiveMqDevice>
{
    
    [Fact]
    public async Task StartAsync_ShouldCreateAndInitializeActiveMqDevices()
    {
        var mockLogger = new Mock<ILogger<DeviceManagerBase<ActiveMqDevice>>>();
        // Arrange
        Configs.Add(CreateMockConfig("MQ_Broker_01"));
        var manager = new TestableManager<ActiveMqDevice>(MockBus.Object, Configs,
            mockLogger.Object, // Assuming you mocked ILogger<DeviceManagerBase<ActiveMqDevice>>
            (config, logger) => new ActiveMqDevice(config, logger)); // The Factory Delegate

        // Act
        await manager.StartAsync(CancellationToken.None);

        // Assert
        var devices = manager.GetDevices();
        devices.Should().HaveCount(1);
        
        
        var mqDevice = devices.First();
        mqDevice.Config.Name.Should().Be("MQ_Broker_01");
        mqDevice.CurrentStateAsString.Should().Be("Connecting");
    }

    [Fact]
    public async Task StopAsync_ShouldMoveDevicesToStoppedState()
    {
        var mockLogger = new Mock<ILogger<DeviceManagerBase<ActiveMqDevice>>>();
        // Arrange
        // Arrange
        Configs.Add(CreateMockConfig("MQ_Broker_01"));
        var manager = new TestableManager<ActiveMqDevice>(MockBus.Object, Configs, mockLogger.Object, // Assuming you mocked ILogger<DeviceManagerBase<ActiveMqDevice>>
            (config, logger) => new ActiveMqDevice(config, logger)); // The Factory Delegate

        await manager.StartAsync(CancellationToken.None);

        // Act
        await manager.StopAsync(CancellationToken.None);

        // Assert
        var mqDevice = manager.GetDevices().First();
        mqDevice.CurrentStateAsString.Should().Be("Stopped");
    }
}