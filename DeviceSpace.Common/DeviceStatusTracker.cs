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
    private ImmutableDictionary<DeviceMetric, long> _lastMetricsSnapshot = ImmutableDictionary<DeviceMetric, long>.Empty;



    // --- High-Precision Timing ---
    private readonly ConcurrentQueue<double> _times = new();
    private readonly ConcurrentDictionary<long, long> _activeTimers = new();
    private const int MAX_ROLLING_WINDOW = 100;

    // --- Public Properties (Restored for Backward Compatibility) ---
    public int CountInbound => (int)GetMetric(DeviceMetric.Inbound);
    public int CountOutbound => (int)GetMetric(DeviceMetric.Outbound);
    public int CountError => (int)GetMetric(DeviceMetric.Error);
    public int CountConnections => (int)GetMetric(DeviceMetric.Conn);
    public int CountDisconnects => (int)GetMetric(DeviceMetric.Disc);
    public double AvgProcessTime => _times.IsEmpty ? 0.0 : _times.Average();

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
    }

    public void IncrementOutbound()
    {
       Increment(DeviceMetric.Outbound);
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
    public void StartTransaction(long id) => _activeTimers[id] = Stopwatch.GetTimestamp();

    public void StopTransaction(long id)
    {
        if (_activeTimers.TryRemove(id, out long startTicks))
        {
            double duration = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            _times.Enqueue(duration);
            while (_times.Count > MAX_ROLLING_WINDOW) _times.TryDequeue(out _);
        }
    }

    public void HeartBeat() => _heartBeatVisual = _heartBeatVisual == 'H' ? 'B' : 'H';

    public bool Update(TState newStatus, TEvent newEvent, DeviceHealth health, string newComments = "")
    {
        var currentMetrics = _metrics.ToImmutableDictionary();
        bool metricsChanged = !currentMetrics.SequenceEqual(_lastMetricsSnapshot);

        if (State.Equals(newStatus) && Event.Equals(newEvent) && Comments == newComments && !metricsChanged)
        {
            return false;
        }

        State = newStatus;
        Event = newEvent;
        Comments = newComments;
        Health = health;
        _lastMetricsSnapshot = currentMetrics;
        return true;
    }

    public void ResetCounters()
    {
        foreach (var key in _metrics.Keys) _metrics[key] = 0;
    }

    public DeviceStatusMessage ToStatusMessage(DeviceKey key)
    {
        // 1. Convert Enum-based metrics to a string-keyed dictionary for the UI
        // This allows the Blazor dashboard to display "PulsesReceived": 500 without knowing the Enum type
        var extraMetrics = _metrics.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => kvp.Value
        );

        // 2. Return the updated message record
        return new DeviceStatusMessage(
            deviceId: key,
            state: State.ToString(),
            health: Health,
            comment: Comments,
            countInbound: CountInbound,
            countOutbound: CountOutbound,
            countConnections: CountConnections,
            countDisconnects: CountDisconnects,
            countError: CountError,
            avgProcessTime: AvgProcessTime,
            hb: _heartBeatVisual,
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