using DeviceTest.Common;
using Device.Connector.Plc;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DeviceTest.Connector.Plc;

public class PlcDeviceManagerTests : DeviceManagerTestBase<PlcMultiClientDevice>
{
    [Fact]
    public async Task StartAsync_ShouldInitializeDevices()
    {
         
        var mockLogger = new Mock<ILogger<DeviceManagerBase<PlcMultiClientDevice>>>();
        // Arrange
        // Arrange
        Configs.Add(CreateMockConfig("PLC_01"));
        var manager = new TestableManager<PlcMultiClientDevice>(MockBus.Object, Configs,  mockLogger.Object, // Assuming you mocked ILogger<DeviceManagerBase<ActiveMqDevice>>
            (config, logger) => new PlcMultiClientDevice(config, logger)); // The Factory Delegate

        // Act
        await manager.StartAsync(CancellationToken.None);

        // Assert
        manager.GetDevices().Should().HaveCount(1);
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateDeviceInstances()
    {
  
        var mockLogger = new Mock<ILogger<DeviceManagerBase<PlcMultiClientDevice>>>();
        // Arrange
        // Arrange
        Configs.Add(CreateMockConfig("PLC_Test"));
        var manager = new TestableManager<PlcMultiClientDevice>(MockBus.Object, Configs,  mockLogger.Object, // Assuming you mocked ILogger<DeviceManagerBase<ActiveMqDevice>>
            (config, logger) => new PlcMultiClientDevice(config, logger)); // The Factory Delegate

        // Act
        await manager.StartAsync(CancellationToken.None);

        // Assert
        manager.GetDevices().Should().HaveCount(1);
    }
}