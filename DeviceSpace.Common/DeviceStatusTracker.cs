using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.Java;
using System.Threading;
using Apache.NMS.ActiveMQ.State;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;

namespace DeviceSpace.Common;

public sealed record DeviceStatusTracker<TState, TEvent> : IDeviceStatusTracker
        where TState : Enum
        where TEvent : Enum
{
    // --- 1. RESTORED COUNTERS (For Devices) ---
    private int _inbound; 
    private int _outbound;
    private int _connections;
    private int _error;
    
    // Last values for "Dirty Check"
    private int _lastInbound; 
    private int _lastOutbound;
    private int _lastConnection;
    private int _lastError;

    private char  HeartBeatVisual = ' '; 

    // --- 2. THREAD-SAFE TIMING (Crash Fixed) ---
    private readonly ConcurrentQueue<double> _times = new();
    private readonly ConcurrentDictionary<long, long> _activeTimers = new();
    
    // --- Public Properties ---
    public DeviceHealth Health { get; set; }
    public TState State { get; private set; }
    public TEvent Event { get; private set; }
    public string Comments { get; private set; }
    
    public int CountInbound => _inbound;
    public int CountOutbound => _outbound;
    public int CountError => _error;
    public int CountConnections => _connections; 
    
    public char GetHeartBeatVisual() => HeartBeatVisual;
    // SAFE AVERAGE: Returns 0 if no transactions have happened yet
    public double AvgProcessTime => !_times.IsEmpty ? _times.Average() : 0.0;

    public DeviceStatusTracker(TState initialState, TEvent initialEvent)
    {
        Comments = "Starting up ....";
        Health = DeviceHealth.Warning;
        State = initialState;
        Event = initialEvent;
    }

    // --- Transaction Logic ---
    public void StartTransaction(long id)
    {
        _activeTimers[id] = Stopwatch.GetTimestamp();
    }

    public void HeartBeat()
    {
        HeartBeatVisual = HeartBeatVisual == 'H' ? 'B' : 'H';
    }
    
 
    public TimeSpan? StopTransaction(long id)
    {
        if (_activeTimers.TryRemove(id, out long startTicks))
        {
            var duration = Stopwatch.GetElapsedTime(startTicks);
            _times.Enqueue(duration.TotalMilliseconds);
            
            // Rolling Window: Keep last 100 items to prevent memory leaks
            while (_times.Count > 100) _times.TryDequeue(out _);
            
            return duration;
        }
        return null;
    }

    public bool Update(TState newStatus, TEvent newEvent , DeviceHealth health, int connected ,  string newComments = "")
    {
        // Optimization: Don't update if nothing changed
        if (State.Equals(newStatus) && Event.Equals(newEvent) && Comments == newComments && 
            _inbound == _lastInbound && _lastOutbound == _outbound && 
            _lastConnection == _connections && _lastError == _error && connected == _connections)
        {
            return false;
        }

        State = newStatus;
        Event = newEvent;
        Comments = newComments;
        Health = health;
        if (connected > -1)
            _connections = connected;
        
        _lastInbound = _inbound;
        _lastError = _error;
        _lastOutbound = _outbound;
        _lastConnection = _connections;
        
        
        return true;
    }

    public DeviceStatusMessage ToStatusMessage(DeviceKey key)
    {
        return new DeviceStatusMessage(
            key, 
            State.ToString(), 
            Health, 
            Comments,
            CountInbound, 
            CountOutbound, 
            CountConnections, 
            CountError,
            AvgProcessTime,
            HeartBeatVisual

        );
    }
    
    
    public override string ToString()
    {
        return $"[{Health}] {State} - {Event}: {Comments}";
    }

    public void SetConnectionCount(int i)
    {
        Interlocked.Exchange(ref _connections, i);
    }
    
    // --- Counter Methods (Devices use these, Managers ignore them) ---
    public void IncrementInbound() => Interlocked.Increment(ref _inbound);
    public void IncrementOutbound() => Interlocked.Increment(ref _outbound);
    public void IncrementConnections() => Interlocked.Increment(ref _connections);
    public void IncrementError(string str)
    {
        Interlocked.Increment(ref _error);
        Comments = str;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _inbound, 0);
        Interlocked.Exchange(ref _outbound, 0);
        Interlocked.Exchange(ref _connections, 0);
        Interlocked.Exchange(ref _error, 0);
    }
}