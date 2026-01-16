using System;
using System.Collections.Generic;
using System.Diagnostics;


namespace DeviceSpace.Utilities;


/// <summary>
/// Thread-safe static utility class for tracking performance metrics (Count, Total Time, Average Time)
/// for different named operations within the Warehouse Control System.
/// </summary>
public static class CoreTimer
{
    // Helper struct to hold statistics for a single operation
    private struct TimerStats
    {
        public Stopwatch Stopwatch { get; set; }
        public long TotalTicks { get; set; }
        public long RunCount { get; set; }
    }

    private static readonly Dictionary<string, TimerStats> Timers = new Dictionary<string, TimerStats>();
    private static readonly object Lock = new object();

    /// <summary>
    /// Starts or restarts the timer for a given operation name.
    /// </summary>
    public static void Start(string name)
    {
        lock (Lock)
        {
            if (!Timers.TryGetValue(name, out TimerStats stats))
            {
                stats = new TimerStats { Stopwatch = new Stopwatch() };
                Timers[name] = stats;
            }
            stats.Stopwatch.Restart();
            Timers[name] = stats; // Reassign struct to dictionary to ensure changes are saved
        }
    }

    /// <summary>
    /// Stops the timer and records the elapsed time, incrementing the run count.
    /// </summary>
    public static void Stop(string name)
    {
        lock (Lock)
        {
            if (Timers.TryGetValue(name, out TimerStats stats))
            {
                stats.Stopwatch.Stop();
                
                // Accumulate statistics
                stats.TotalTicks += stats.Stopwatch.ElapsedTicks;
                stats.RunCount++;
                
                Timers[name] = stats; // Save updated struct
            }
        }
    }

    /// <summary>
    /// Gets the raw elapsed time for the current, running cycle of the specified timer.
    /// </summary>
    public static TimeSpan GetElapsedTime(string name)
    {
        lock (Lock)
        {
            if (Timers.TryGetValue(name, out TimerStats stats))
            {
                return stats.Stopwatch.Elapsed;
            }
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Calculates the average elapsed time in milliseconds for the specified operation.
    /// </summary>
    /// <returns>Average time in milliseconds, or 0 if no runs have been recorded.</returns>
    public static double GetAverageMilliseconds(string name)
    {
        lock (Lock)
        {
            if (Timers.TryGetValue(name, out TimerStats stats) && stats.RunCount > 0)
            {
                // Convert ticks to milliseconds using the frequency
                double totalMs = (double)stats.TotalTicks / Stopwatch.Frequency * 1000;
                return totalMs / stats.RunCount;
            }
            return 0.0;
        }
    }
    
    /// <summary>
    /// Clears the recorded statistics (Total Time and Count) for a specific timer.
    /// Does not stop the stopwatch if it is currently running.
    /// </summary>
    public static void Clear(string name)
    {
        lock (Lock)
        {
            if (Timers.TryGetValue(name, out TimerStats stats))
            {
                stats.TotalTicks = 0;
                stats.RunCount = 0;
                // Note: We don't call stats.Stopwatch.Reset() as it also stops the timer.
                Timers[name] = stats;
            }
        }
    }

    /// <summary>
    /// Prints all recorded timer statistics (Total, Count, and Average) to the console.
    /// </summary>
    public static void PrintAll()
    {
        lock (Lock)
        {
            foreach (var pair in Timers)
            {
                TimerStats stats = pair.Value;
                double totalMs = (double)stats.TotalTicks / Stopwatch.Frequency * 1000;
                double averageMs = (stats.RunCount > 0) ? totalMs / stats.RunCount : 0.0;

                Console.WriteLine(
                    $"Timer '{pair.Key}' | Total Runs: {stats.RunCount} | Total Time: {totalMs:F2} ms | Avg Latency: {averageMs:F4} ms"
                );
            }
        }
    }
}