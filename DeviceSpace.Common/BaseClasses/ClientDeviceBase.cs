using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;

/// <summary>
/// Abstract base class for client-side devices providing built-in state management, 
/// reconnection logic, automated heartbeats, and metric tracking.
/// </summary>
public abstract class ClientDeviceBase : DeviceBase<ClientDeviceBase.State, ClientDeviceBase.Event, DeviceMetric>
{
    public enum State
    {
        Offline,
        Starting,
        Connecting,
        Connected,
        Processing,
        Faulted,
        ServerOffline,
        Stopping
    }

    public enum Event
    {
        Start,
        Stop,
        ConnectSuccess,
        ConnectFailed,
        DataReceived,
        MessageSent,
        ConnectionLost,
        Error
    }

    private CancellationTokenSource? _heartbeatCts;
    private int _connectionAttempts = 0;
    private DateTime _lastHeartbeatResponse = DateTime.MinValue;

    protected ClientDeviceBase(IDeviceConfig config, ILogger logger, bool 
        needsHb = false)
        : base(config, logger, State.Offline, Event.Start)
    {
        NeedsHeartbeat = needsHb;
        ConfigureStateMachine();
    }

    // --- Abstract Requirements ---
    public abstract Task SendAsync(string message, CancellationToken token);
    public abstract Task SendHeartbeatAsync(CancellationToken token);
    protected abstract void DeviceFaultedAsync(CancellationToken token = default);
    protected abstract Task ConnectAsync( CancellationToken ct = default);
    protected abstract Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct);

    protected virtual void DeviceConnected() { }
    // --- State Machine Configuration ---

   protected sealed override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .OnEntry(() => Tracker.SetConnectionCount(0))
            .Permit(Event.Start, State.Connecting);

        Machine.Configure(State.Connecting)
            .OnEntry(t =>
            {
                _connectionAttempts++;
                Logger.Information("[{Dev}] Connection attempt #{Count}", Config.Name, _connectionAttempts);
                _ = ConnectAsync();
            })
            .Permit(Event.ConnectSuccess, State.Connected)
            .Permit(Event.ConnectFailed, State.ServerOffline)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Connected)
            .OnEntry(() =>
            {
 
                Tracker.IncrementConnections();
                Tracker.SetConnectionCount(1);
                _connectionAttempts = 0;
                _lastHeartbeatResponse = DateTime.UtcNow; // Reset on connect
                if (NeedsHeartbeat)
                    StartHeartbeat(); 
            })
            .OnExit(() =>
            {
                Tracker.IncrementDisconnects();
                Tracker.SetConnectionCount(0);
                if (NeedsHeartbeat)
                    StopHeartbeat();
            })
            .InternalTransition(Event.MessageSent, () => Tracker.IncrementOutbound())
            .InternalTransition(Event.DataReceived, () => Tracker.IncrementInbound())
            .Permit(Event.ConnectionLost, State.ServerOffline)
            .Permit(Event.Error, State.Faulted)
            .Permit(Event.Stop, State.Stopping);

        Machine.Configure(State.ServerOffline)
            .OnEntry(t =>
            {
                Logger.Warning("[{Dev}] Server offline. Retrying in 5s...", Config.Name);
                _ = Task.Delay(5000).ContinueWith(_ => Machine.Fire(Event.Start));
            })
            .Permit(Event.Start, State.Connecting)
            .Permit(Event.Stop, State.Offline);
        
        Machine.Configure(State.Faulted)
            .OnEntry(_ => 
            {
                Tracker.IncrementError("Device Faulted");
                DeviceFaultedAsync();
            })
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Stopping)
            .OnEntry(() => StopHeartbeat())
            .Permit(Event.Stop, State.Offline);

        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            var ex = new InvalidOperationException($"Invalid transition: {trigger} in {state}");
            OnError("StateMachine_LogicGap", ex);
        });
    }
    // --- Heartbeat Logic ---

    protected virtual void InitHeartbeat() { }

    protected virtual void EndHeartbeat() {}


    private void StartHeartbeat()
    {
        InitHeartbeat();
        _heartbeatCts?.Cancel();
        _heartbeatCts = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    private void StopHeartbeat()
    {
        EndHeartbeat();
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
    }

   private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        int interval = ConfigurationLoader.GetOptionalConfig(Config.Properties, "HeartbeatInterval", 5000);
        int timeout = ConfigurationLoader.GetOptionalConfig(Config.Properties, "HeartbeatTimeout", 15000);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);

                if (Machine.State == State.Connected || Machine.State == State.Processing)
                {
                    // 1. Check for timeout (Active Heartbeat)
                    var timeSinceLastResponse = (DateTime.UtcNow - _lastHeartbeatResponse).TotalMilliseconds;
                    if (timeSinceLastResponse > timeout)
                    {
                        Logger.Error("[{Dev}] Heartbeat timeout! No response for {ms}ms", Config.Name, timeSinceLastResponse);
                        await Machine.FireAsync(Event.ConnectionLost);
                        break;
                    }

                    // 2. Send the next heartbeat
                    try
                    {
                        await SendHeartbeatAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "[{Dev}] Heartbeat send failure.", Config.Name);
                        await Machine.FireAsync(Event.ConnectionLost);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
   
  
    /// <summary>
    /// Derived classes must call this method when a valid heartbeat response is detected 
    /// from the remote server or PLC.
    /// </summary>
   protected void NotifyHeartbeatReceived(string s, string s1)
    {
        _lastHeartbeatResponse = DateTime.UtcNow;
        Tracker.HeartBeat(); // Toggles visual H/B on dashboard
        UpdateAndNotify();
    }
    // --- Core Lifecycle Overrides ---

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Information("[{Dev}] WCS Service Starting...", Config.Name);

        // 1. Trigger the 'Start' event in your State Machine
        await Machine.FireAsync(Event.Start);
    }

    public override async Task StopAsync(CancellationToken token) => await Machine.FireAsync(Event.Stop);

    public override void OnError(string context, Exception ex)
    {
        Logger.Error(ex, "[{Dev}] Error in {Context}. State: {State}", Config.Name, context, Machine.State);
        Tracker.IncrementError(ex.Message);
        UpdateAndNotify();
        if (Machine.CanFire(Event.Error)) Machine.Fire(Event.Error);
    }

    protected override DeviceHealth MapStateToHealth(State state)
    {
        return state switch
        {
            State.Processing => DeviceHealth.Normal,
            State.Connected => DeviceHealth.Normal,
            State.Offline => DeviceHealth.Warning,
            State.Connecting => DeviceHealth.Warning,
            State.ServerOffline => DeviceHealth.Warning,
            State.Faulted => DeviceHealth.Critical,
            _ => DeviceHealth.Warning
        };
    }
}