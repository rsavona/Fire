using System.Collections.Concurrent;
using Serilog;
using Serilog.Events;

namespace DeviceSpace.Common.Logging;

public static class SmartLogger
{
    private static readonly ConcurrentDictionary<string, int> _counters = new();

    /// <summary>
    /// Logs only every 'n-th' time for a specific key (e.g., "PLC_Poll")
    /// but always logs if the level is Warning or higher.
    /// </summary>
    public static void LogSampled(string key, int sampleRate, string subject, string verb, string id, string obj, string comment, LogEventLevel level = LogEventLevel.Information)
    {
        if (level >= LogEventLevel.Warning)
        {
            Log.Logger.FireLogError(subject, verb, id, obj, comment);
            return;
        }

        int current = _counters.AddOrUpdate(key, 1, (k, val) => val + 1);

        if (current % sampleRate == 0)
        {
            Log.Logger.FireLogInfo(subject, verb, id, obj, $"{comment} (Sampled 1/{sampleRate})");
            _counters.TryUpdate(key, 0, current); // Reset counter
        }
    }
}