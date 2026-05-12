using System.Collections.Concurrent;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace DeviceSpace.Common.logging;

public class BufferedLog : ILogEventSink
{
    // Circular buffer to hold the last 100 trace/debug events
    private readonly ConcurrentQueue<LogEvent> _buffer = new();
    private const int MaxBufferSize = 100;

    public void Emit(LogEvent logEvent)
    {
        // Avoid re-buffering events that are being dumped
        if (logEvent.Properties.ContainsKey("BufferedDump")) return;

        // 0. Disable Smart Dump if loglevel is Verbose (vrb)
        // If we are already logging everything, the forensic dump is redundant.
        if (logEvent.Properties.TryGetValue("DeviceName", out var propertyValue) &&
            propertyValue is ScalarValue scalarValue &&
            scalarValue.Value is string deviceName)
        {
            if (LogControl.IsVerbose(deviceName))
            {
                if (!_buffer.IsEmpty) _buffer.Clear();
                return;
            }
        }

        // 1. If it's an Error, "Dump" the buffer to the real log immediately
        if (logEvent.Level >= LogEventLevel.Error)
        {
            DumpBuffer();
            return;
        }

        // 2. Otherwise, if it's a Trace (Verbose) or Debug, just store it in memory
        if ( logEvent.Level == LogEventLevel.Debug || logEvent.Level == LogEventLevel.Verbose)
        {
            _buffer.Enqueue(logEvent);

            // Keep it circular: remove oldest if we exceed capacity
            while (_buffer.Count > MaxBufferSize)
            {
                _buffer.TryDequeue(out _);
            }
        }
    }

    private void DumpBuffer()
    {
        if (_buffer.IsEmpty) return;

        Log.Logger.Warning("!!! [SMART DUMP] Error detected. Dumping last {Count} trace events for forensics:", _buffer.Count);
        
        while (_buffer.TryDequeue(out var e))
        {
            // Add the property so this sink ignores it when re-emitted
            e.AddOrUpdateProperty(new LogEventProperty("BufferedDump", new ScalarValue(true)));
            Log.Logger.Write(e);
        }
        
        Log.Logger.Warning("!!! [SMART DUMP] End of forensic dump.");
    }
}
