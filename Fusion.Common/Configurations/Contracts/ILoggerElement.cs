namespace Fusion.Common.Contracts;

public interface ILoggerElement :  IElement
{
    string ElementName { get; }
    void Initialize(string configurationPath);
    void Log(string level, string message);
    void Shutdown();
}