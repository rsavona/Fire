using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;

namespace DeviceSpace.Common;

public sealed record DeviceStatusTracker<TState, TEvent> : IDeviceStatusTracker
    where TState : struct, Enum
    where TEvent : struct, Enum

{
    private readonly ConcurrentDictionary<DeviceMetric, long> _metrics = new();
    private readonly ConcurrentDictionary<(string strId, long nId), long> _activeTimers = new();
     private readonly ConcurrentDictionary<long, long> _activeLongTimers = new();
    private ImmutableDictionary<DeviceMetric, long>
        _lastMetricsSnapshot = ImmutableDictionary<DeviceMetric, long>.Empty;


    // --- High-Precision Timing ---
    private readonly ConcurrentQueue<double> _times = new();
    
    private const int MAX_ROLLING_WINDOW = 100;

    // --- Rate Logic ---
    private readonly RollingRateCounter _inboundRate = new();
    private readonly RollingRateCounter _outboundRate = new();

    // --- Public Properties (Restored for Backward Compatibility) ---
    public int CountInbound => (int)GetMetric(DeviceMetric.Inbound);
    public int CountOutbound => (int)GetMetric(DeviceMetric.Outbound);
    public int CountError => (int)GetMetric(DeviceMetric.Error);
    public int CountConnections => (int)GetMetric(DeviceMetric.Conn);
    public int CountDisconnects => (int)GetMetric(DeviceMetric.Disc);
    public double AvgProcessTime => _times.IsEmpty ? 0.0 : _times.Average();
    public double InboundRate => _inboundRate.GetRate();
    public double OutboundRate => _outboundRate.GetRate();

    public int ScreenIndex { get; set; } = 0;
    public DeviceHealth Health { get; set; }
    public TState State { get; private set; }
    public TEvent Event { get; private set; }

    public string Comments { get; private set; }

    private char _heartBeatVisual = ' ';

    public DeviceStatusTracker(
        TState initialState,
        TEvent initialEvent)
    {
        State = initialState;
        Event = initialEvent;
        Comments = "Starting up ....";
        Health = DeviceHealth.Warning;


        foreach (DeviceMetric metric in Enum.GetValues<DeviceMetric>())
        {
            _metrics[metric] = 0;
        }

        _lastMetricsSnapshot = _metrics.ToImmutableDictionary();
    }

    // --- Metric Logic ---
    public void Increment(DeviceMetric metric) => _metrics.AddOrUpdate(metric, 1, (_, val) => val + 1);

    private long GetMetric(DeviceMetric? key)
    {
        if (key.HasValue && _metrics.TryGetValue(key.Value, out var val)) return val;
        return 0;
    }

    // --- Increment Wrappers ---
    public void IncrementInbound()
    {
        Increment(DeviceMetric.Inbound);
        _inboundRate.AddEvent();
    }

    public void IncrementOutbound()
    {
        Increment(DeviceMetric.Outbound);
        _outboundRate.AddEvent();
    }

    public void IncrementConnections()
    {
        Increment(DeviceMetric.Conn);
    }

    public void IncrementDisconnects()
    {
        Increment(DeviceMetric.Disc);
    }

    public void IncrementError(string str)
    {
        Increment(DeviceMetric.Error);
    }

    // --- Transaction Timing Logic ---
    public void StartTransaction(string strId, long nId) => _activeTimers[(strId, nId)] = Stopwatch.GetTimestamp();
     public void StartTransaction( long nId) => _activeLongTimers[ nId ] = Stopwatch.GetTimestamp();

    public double StopTransaction(string strId, long nId)
    {
        double duration = 0.0;
        if (_activeTimers.TryRemove((strId, nId), out long startTicks))
        {
            duration = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            _times.Enqueue(duration);
            while (_times.Count > MAX_ROLLING_WINDOW) _times.TryDequeue(out _);
        }
        return duration;
    }
     public double StopTransaction(long nId)
    {
        double duration = 0.0;
        if (_activeLongTimers.TryRemove(nId, out long startTicks))
        {
            duration = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            _times.Enqueue(duration);
            while (_times.Count > MAX_ROLLING_WINDOW) _times.TryDequeue(out _);
        }
        return duration;
    }
    
    public void HeartBeat() => _heartBeatVisual = _heartBeatVisual == 'H' ? 'B' : 'H';

    public bool Update(TState newStatus, TEvent newEvent, DeviceHealth health, string newComments = "")
    {
  
        if (State.Equals(newStatus) && Event.Equals(newEvent) && Comments == newComments && Health == health)
        {
            return false;
        }

        State = newStatus;
        Event = newEvent;
        if(!newComments.IsWhiteSpace() || newComments != "")
            Comments = newComments;
        Health = health;
       
        return true;
    }

    public bool Update(TState newStatus, TEvent newEvent, string newComments = "")
    {
       
        var calculatedHealth = CountError == 0 ? DeviceHealth.Normal : DeviceHealth.Warning;

        if (State.Equals(newStatus) && Event.Equals(newEvent) && Comments == newComments && Health == calculatedHealth)
        {
            return false;
        }
        // 3. Apply updates
        State = newStatus;
        Event = newEvent;
        if(!newComments.IsWhiteSpace() || newComments != "")
            Comments = newComments;
        Health = calculatedHealth; // Assign the calculated health

        return true;
    }

    public void ResetCounters()
    {
        foreach (var key in _metrics.Keys) _metrics[key] = 0;
    }

    public DeviceStatusMessage ToStatusMessage(DeviceKey key, string comment = "", int resTasks = 0, int resContainers = 0, int resDeepCount = 0)
    {
        // 1. Convert Enum-based metrics to a string-keyed dictionary for the UI
        // This allows the Blazor dashboard to display "PulsesReceived": 500 without knowing the Enum type
        var extraMetrics = _metrics.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => kvp.Value
        );
        if (comment != "")
            Comments = comment;

        // 2. Return the updated message record
        return new DeviceStatusMessage(
            deviceId: key,
            state: State.ToString(),
            health: Health,
            comment: Comments,
            screenIndex: ScreenIndex,
            countInbound: CountInbound,
            countOutbound: CountOutbound,
            countConnections: CountConnections,
            countDisconnects: CountDisconnects,
            countError: CountError,
            inboundRate: InboundRate,
            outboundRate: OutboundRate,
            avgProcessTime: AvgProcessTime,
            hb: _heartBeatVisual,
            resTasks: resTasks,
            resContainers: resContainers,
            resDeepCount: resDeepCount,
            extraMetrics: extraMetrics // Pass the dynamic metrics here
        );
    }

    public char GetHeartBeatVisual()
    {
        return _heartBeatVisual;
    }

    public void SetConnectionCount(int connectedClientsCount)
    {
        // Explicitly set the value for the connection metric
        _metrics[DeviceMetric.Conn] = connectedClientsCount;
    }
}

/// <summary>
/// Helper to track rolling average rate per second over a 1-minute window.
/// </summary>
internal class RollingRateCounter
{
    private readonly ConcurrentQueue<long> _events = new();
    private const int WindowSeconds = 60;

    public void AddEvent()
    {
        _events.Enqueue(Stopwatch.GetTimestamp());
        CleanUp();
    }

    public double GetRate()
    {
        CleanUp();
        if (_events.IsEmpty) return 0.0;
        return (double)_events.Count / WindowSeconds;
    }

    private void CleanUp()
    {
        long cutoff = Stopwatch.GetTimestamp() - (WindowSeconds * Stopwatch.Frequency);
        while (_events.TryPeek(out long timestamp) && timestamp < cutoff)
        {
            _events.TryDequeue(out _);
        }
    }
}
