using Moq;
using DeviceSpace.Common.Contracts;
using Microsoft.Extensions.Logging;


namespace DeviceTest.Common;

public abstract class DeviceManagerTestBase<TDevice> where TDevice : class, IDevice
{
    protected readonly Mock<IMessageBus> MockBus = new();
    protected readonly Mock<ILoggerFactory> MockLoggerFactory = new();
    protected List<IDeviceConfig> Configs = new();

    protected DeviceManagerTestBase()
    {
        MockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<Microsoft.Extensions.Logging.ILogger>().Object);
    }

    // Centralized helper to create valid mock configs for tests
    protected IDeviceConfig CreateMockConfig(string name)
    {
        var mock = new Mock<IDeviceConfig>();
        mock.SetupGet(c => c.Name).Returns(name);
        mock.SetupGet(c => c.Enable).Returns(true);
        mock.SetupGet(c => c.Properties).Returns(new Dictionary<string, object>());
        return mock.Object;
    }
}