namespace DeviceSpace.Common.Contracts;

public interface ILoggerDevice :  IDevice
{
    string DeviceName { get; }
    void Initialize(string configurationPath);
    void Log(string level, string message);
    void Shutdown();
}