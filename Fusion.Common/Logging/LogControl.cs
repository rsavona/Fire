using Serilog.Events;
using Serilog.Core;
using System;
using System.Collections.Concurrent;

public static class LogControl
{
    // The global level switch for the entire application
    public static readonly LoggingLevelSwitch LevelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

    // Toggle for console logging. Start disabled (High level)
    public static readonly LoggingLevelSwitch ConsoleLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Fatal + 1);

    // A thread-safe dictionary mapping ElementName -> LogEventLevel
    private static readonly ConcurrentDictionary<string, LogEventLevel> _elementLogLevels = 
        new ConcurrentDictionary<string, LogEventLevel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Call this from anywhere in your app to change a element's log level at runtime.
    /// Example: LogControl.SetElementLevel("PNA1", LogEventLevel.Debug);
    /// </summary>
    public static void SetElementLevel(string elementName, LogEventLevel level)
    {
        _elementLogLevels[elementName] = level;
    }

    /// <summary>
    /// Returns true if the element (or the global system) is currently set to Verbose logging.
    /// Used to disable "Smart Dump" when full tracing is already active.
    /// </summary>
    public static bool IsVerbose(string elementName)
    {
        // 1. Check Global Level
        if (LevelSwitch.MinimumLevel == LogEventLevel.Verbose) return true;

        // 2. Check Element Level
        return _elementLogLevels.TryGetValue(elementName, out var level) && level == LogEventLevel.Verbose;
    }

    /// <summary>
    /// The filter your Serilog Pipeline 2 calls for every element log.
    /// </summary>
    public static bool DynamicFilter(LogEvent logEvent)
    {
        // 1. Safely extract the "ElementName" property from the log event
        if (logEvent.Properties.TryGetValue("ElementName", out var propertyValue) &&
            propertyValue is ScalarValue scalarValue &&
            scalarValue.Value is string elementName)
        {
            // 2. Look up the assigned level for this element. 
            // If it hasn't been explicitly set, default to Information.
            var requiredLevel = _elementLogLevels.TryGetValue(elementName, out var level) 
                ? level 
                : LogEventLevel.Information;

            // 3. Return true (log it) if the event meets or exceeds the required level
            return logEvent.Level >= requiredLevel;
        }

        return true; 
    }
}