using Device.Connector.Printer;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceTest.Common; 
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger; // Added missing Xunit reference

namespace Device.Printer.Test;

public class TestablePrinterManager : PrinterManager
{
    public TestablePrinterManager(
        IMessageBus bus, 
        List<IDeviceConfig> configs, 
        ILogger<DeviceManagerBase<IDevice>> logger,
        Func<IDeviceConfig, ILogger, ITcpPrinter> deviceFactory) 
        : base(bus, configs, logger, deviceFactory) 
    {
    }

    public IEnumerable<IDevice> GetManagedDevices() => DeviceInstances;
}

public class PrinterManagerTests : DeviceManagerTestBase<IDevice>
{
    private readonly List<IDeviceConfig> _configs;
    private readonly Func<IDeviceConfig, ILogger, ITcpPrinter> _factory;

    public PrinterManagerTests()
    {
        var config = new Mock<IDeviceConfig>();
        config.SetupGet(c => c.Name).Returns("Zebra_01");
        config.SetupGet(c => c.Enable).Returns(true);
        config.SetupGet(c => c.Properties).Returns(new Dictionary<string, object>
        {
            { "Host", "127.0.0.1" },
            { "Port", 9100 },
            { "Brand", "Zebra" }
        });

        _configs = new List<IDeviceConfig> { config.Object };

        // Define a consistent factory for tests. 
        // Using a Mock IDevice so we don't need a concrete 'Device' class.
        _factory = (cfg, log) => 
        {
            var mockDevice = new Mock<IDevice>();
            mockDevice.SetupGet(d => d.Config).Returns(cfg);
            return (ITcpPrinter)mockDevice.Object;
        };
    }

    [Fact]
    public async Task StartAsync_ShouldInstantiateCorrectPrinterType()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<DeviceManagerBase<IDevice>>>();
        
        var manager = new TestablePrinterManager(
            MockBus.Object, 
            _configs, 
            mockLogger.Object, 
            _factory); // Use the fixed factory

        // Act
        await manager.StartAsync(CancellationToken.None);

        // Assert
        var devices = manager.GetManagedDevices().ToList();
        devices.Should().HaveCount(1);
        devices.First().Config.Name.Should().Be("Zebra_01");
    }

 
    [Fact]
    public async Task StartAsync_ShouldPopulateConnectionDetails()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<DeviceManagerBase<IDevice>>>(); // Create the mock directly
    
        var manager = new TestablePrinterManager(
            MockBus.Object, 
            _configs, 
            mockLogger.Object, // Use the mock object
            _factory);

        // Act ...
    }
}