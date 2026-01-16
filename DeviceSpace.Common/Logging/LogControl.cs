using System.Collections.Concurrent;
using Serilog.Events;

public static class LogControl
{
    // Global fallback level (if a specific device isn't set)
    public static LogEventLevel GlobalLevel { get; set; } = LogEventLevel.Information;

    // Dictionary to hold per-device levels: "Scanner01" -> Debug
    public static ConcurrentDictionary<string, LogEventLevel> DeviceLevels { get; } 
        = new ConcurrentDictionary<string, LogEventLevel>();

    // THE FILTER LOGIC
    // Returns TRUE if the log should be written, FALSE if it should be dropped.
    public static bool DynamicFilter(LogEvent logEvent)
    {
        // 1. Check if the log has a "DeviceName" property
        if (logEvent.Properties.TryGetValue("DeviceName", out var value) && 
            value is ScalarValue scalar && 
            scalar.Value is string deviceName)
        {
            // 2. Look for a specific level for this device
            if (DeviceLevels.TryGetValue(deviceName, out var deviceLevel))
            {
                return logEvent.Level >= deviceLevel;
            }
        }

        // 3. Fallback to global level
        return logEvent.Level >= GlobalLevel;
    }
}