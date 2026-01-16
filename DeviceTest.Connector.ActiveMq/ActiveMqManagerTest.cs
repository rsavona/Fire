using Device.Connector.ActiveMQ;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ILogger = Serilog.ILogger;

namespace DeviceTest.Connector.ActiveMq;
public class TestablePlcManager : ActiveMqManager
{
    public TestablePlcManager(IMessageBus bus, List<IDeviceConfig> configs, ILogger<DeviceManagerBase<ActiveMqDevice>> logger, 
            Func<IDeviceConfig, ILogger, ActiveMqDevice> deviceFactory)
        : base(bus, configs, logger, deviceFactory) { }

    // Expose the protected Devices collection to the test
    public IEnumerable<ActiveMqDevice> GetDevices() => DeviceInstances;
}

public partial class ActiveMqManagerTests
{
    private readonly Mock<IMessageBus> _mockBus;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly List<IDeviceConfig> _configs;

    public ActiveMqManagerTests()
    {
        _mockBus = new Mock<IMessageBus>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        
        // Setup logger factory to return a dummy logger
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
                          .Returns(new Mock<Microsoft.Extensions.Logging.ILogger>().Object);

        // Setup a mock configuration for an ActiveMQ Device
        var config = new Mock<IDeviceConfig>();
        config.Setup(c => c.Name).Returns("MQ_Broker_01");
        config.Setup(c => c.Properties).Returns(new Dictionary<string, object>
        {
            { "ConnectionString", "activemq:tcp://localhost:61616" },
            { "LogLevel", "Information" }
        });

        _configs = new List<IDeviceConfig> { config.Object };
    }

  
}