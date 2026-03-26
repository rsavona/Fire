using Serilog.Events;
using System;
using System.Collections.Concurrent;

public static class LogControl
{
    // A thread-safe dictionary mapping DeviceName -> LogEventLevel
    private static readonly ConcurrentDictionary<string, LogEventLevel> _deviceLogLevels = 
        new ConcurrentDictionary<string, LogEventLevel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Call this from anywhere in your app to change a device's log level at runtime.
    /// Example: LogControl.SetDeviceLevel("PNA1", LogEventLevel.Debug);
    /// </summary>
    public static void SetDeviceLevel(string deviceName, LogEventLevel level)
    {
        _deviceLogLevels[deviceName] = level;
    }

    /// <summary>
    /// The filter your Serilog Pipeline 2 calls for every device log.
    /// </summary>
    public static bool DynamicFilter(LogEvent logEvent)
    {
        // 1. Safely extract the "DeviceName" property from the log event
        if (logEvent.Properties.TryGetValue("DeviceName", out var propertyValue) &&
            propertyValue is ScalarValue scalarValue &&
            scalarValue.Value is string deviceName)
        {
            // 2. Look up the assigned level for this device. 
            // If it hasn't been explicitly set, default to Information.
            var requiredLevel = _deviceLogLevels.TryGetValue(deviceName, out var level) 
                ? level 
                : LogEventLevel.Information;

            // 3. Return true (log it) if the event meets or exceeds the required level
            return logEvent.Level >= requiredLevel;
        }

        return true; 
    }
}