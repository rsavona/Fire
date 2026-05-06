namespace DeviceSpace.Common.Contracts;

public interface ILogBusProvider
{
    void LogInfo(string messageTemplate, params object?[]? propertyValues);
    void LogError(string messageTemplate, params object?[]? propertyValues);
    void LogDebug(string messageTemplate, params object?[]? propertyValues);
    void LogWarning(string messageTemplate, params object?[]? propertyValues);
    void LogFatal(string messageTemplate, params object?[]? propertyValues);
    void LogTrace(string messageTemplate, params object?[]? propertyValues);
    void LogException(Exception ex, string messageTemplate, params object?[]? propertyValues);    
}