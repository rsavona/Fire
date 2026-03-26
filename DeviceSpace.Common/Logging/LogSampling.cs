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
    public static void LogSampled(string key, int sampleRate, string subject, string verb, string id, string obj,
        string comment, LogEventLevel level = LogEventLevel.Information)
    {
        // 1. Correctly route Warnings to Warning (not Error), and skip sampling for criticals
        if (level == LogEventLevel.Warning)
        {
            Log.Logger.Warning(subject, verb, id, obj, comment);
            return;
        }

        if (level >= LogEventLevel.Error)
        {
            Log.Logger.Error(subject, verb, id, obj, comment);
            return;
        }

        // 2. The Atomic Fix: Reset the counter inside the AddOrUpdate delegate natively.
        // This prevents the race condition and guarantees the counter never exceeds the sampleRate.
        int current = _counters.AddOrUpdate(
            key,
            1,
            (_, val) => val >= sampleRate ? 1 : val + 1
        );

        // 3. Log exactly when we hit the threshold
        if (current == sampleRate)
        {
            Log.Logger.Information(subject, verb, id, obj, $"{comment} (Sampled 1/{sampleRate})");
        }
        else
        {
            // 4. (Recommended) Drop the noise to Verbose so it isn't lost forever
            // Log.Logger.FireLogVerbose(subject, verb, id, obj, comment);
        }
    }
}