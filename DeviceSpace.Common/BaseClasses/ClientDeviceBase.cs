using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using System.Diagnostics;
using Serilog.Core;
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
        WaitingToRetry,
        Connecting,
        Connected,
        Unavailable,
        Faulted,
        NoActivity,
        ServerOffline,
        Stopping
    }

    public enum Event
    {
        Start,
        Stop,
        ConnectSuccess,
        ConnectFailed,
        ConnectRetry,
        MessageReceived,
        MessageSent,
        ConnectionLost,
        NoResponse,
        Error,
        MakeUnavailable,
        MakeAvailable
    }

    private CancellationTokenSource? _periodicEventCancellation;
    private int _connectionAttempts;
    private DateTime _lastResponse = DateTime.MinValue;
    protected CancellationToken _shutdownToken;
    private int _interval;
    private int _timeout;


    protected ClientDeviceBase(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch ls, bool
        needsHb = false)
        : base(config, logger, ls, State.Offline, Event.Start)
    {
        _interval = ConfigurationLoader.GetOptionalConfig(Config.Properties, "HeartbeatIntervalMs", 5000);
        
        // Safety check: Prevent 0ms interval from causing infinite spin loops
        if (_interval < 100) _interval = 100;

        _timeout = ConfigurationLoader.GetOptionalConfig(Config.Properties, "HeartbeatTimeoutMs", 15000);
        
        if (_timeout > 0)
            NeedsHeartbeat = needsHb;
        else
            NeedsHeartbeat = false;
        
        ConfigureStateMachine();
    }

    // --- Abstract Requirements ---
    public abstract Task SendAsync(string message, CancellationToken token, bool fireEvent = true);
    public abstract Task SendHeartbeatAsync(CancellationToken token);
    protected abstract void OnDeviceFaultedAsync(CancellationToken token = default);
    protected abstract Task<bool> ConnectAsync(CancellationToken ct = default);


    // ------ optional virtual methods ----
    protected override Task OnStartAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    protected virtual Task DeviceConnectedAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task OnNoActivityAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task DeviceDisconnectedAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task OnDeviceStoppingAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task DeviceOfflineAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task DeviceServerOfflineAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task InitPeriodicEvent()
    {
        return Task.CompletedTask;
    }

    protected virtual void EndPeriodicEvent()
    {
    }

    protected virtual void OnDeviceUnavailable()
    {
    }

    private void StartPeriodicEvent()
    {
        InitPeriodicEvent();
        _periodicEventCancellation?.Cancel();
        _periodicEventCancellation = new CancellationTokenSource();
        RegisterTask(HeartbeatLoopAsync(_periodicEventCancellation.Token));
    }

    private void StopHeartbeat()
    {
        EndPeriodicEvent();
        _periodicEventCancellation?.Cancel();
        _periodicEventCancellation?.Dispose();
        _periodicEventCancellation = null;
    }

    // --- State Machine Configuration ---

    protected sealed override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .OnEntry(() =>
            {
                Tracker.SetConnectionCount(0);
                DeviceOfflineAsync();
            })
            .Permit(Event.Start, State.Connecting);

        Machine.Configure(State.Connecting)
            .OnEntry(t =>
            {
                _connectionAttempts++;
                Logger.Information("[{Dev}] Connection attempt #{Count}", Config.Name, _connectionAttempts);
                _ = Task.Run(ExecuteConnect); // Avoid async void
            })
            .Permit(Event.ConnectSuccess, State.Connected)
            .Permit(Event.ConnectFailed, State.WaitingToRetry)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.WaitingToRetry)
            .OnEntry(async t =>
            {
                Tracker.SetConnectionCount(0);
                
                // Exponential backoff: Double the interval for each attempt, cap at 60 seconds
                int backoffMs = Math.Min(_interval * (int)Math.Pow(2, Math.Min(_connectionAttempts - 1, 6)), 60000);
                
                Logger.Information("[{Dev}] Waiting {ms}ms before next reconnection attempt.", Config.Name, backoffMs);
                
                await Task.Delay(backoffMs, _shutdownToken);
                
                if (!_shutdownToken.IsCancellationRequested)
                {
                    await Machine.FireAsync(Event.ConnectRetry);
                }
            })
            .Permit(Event.ConnectRetry, State.Connecting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Connected)
            .OnEntry(() =>
            {
                Tracker.SetConnectionCount(1);
                _connectionAttempts = 0;
                _ = DeviceConnectedAsync();
                _lastResponse = DateTime.UtcNow; // Reset on connect
                if (NeedsHeartbeat)
                    StartPeriodicEvent();
            })
            .OnExit(() =>
            {
                Tracker.SetConnectionCount(0);
                _ = DeviceDisconnectedAsync();
                if (NeedsHeartbeat)
                    StopHeartbeat();
            })
            .InternalTransition(Event.MessageReceived, () =>
            {
                Tracker.IncrementInbound();
                UpdateAndNotify();
            })
            .InternalTransition(Event.MessageSent, () =>
            {
                Tracker.IncrementOutbound();
                UpdateAndNotify();
            })

            .Permit(Event.MakeUnavailable, State.Unavailable)
            .Permit(Event.ConnectionLost, State.ServerOffline)
            .Permit(Event.NoResponse, State.NoActivity)
            .Permit(Event.Error, State.Faulted)
            .Permit(Event.Stop, State.Stopping);

        Machine.Configure(State.Unavailable)
            .SubstateOf(State.Connected) // Inherits ConnectionLost, Error, Stop, etc.
            .OnEntry(() =>
            {
                Logger.Warning("[{Dev}] Device entered UNAVAILABLE substate.", Config.Name);
                UpdateAndNotify();
            })
            .OnExit(() =>
            {
                Logger.Information("[{Dev}] Device leaving UNAVAILABLE substate.", Config.Name);
                UpdateAndNotify();
            })
            // Return to the main Connected state
            .Permit(Event.MakeAvailable, State.Connected)
            // Optional: You can explicitly ignore sends while unavailable
            .Ignore(Event.MessageSent);

        Machine.Configure(State.ServerOffline)
            .OnEntry(t =>
            {
                DeviceServerOfflineAsync();
                Logger.Warning("[{Dev}] Server offline. Retrying in 5s...", Config.Name);
                _ = Task.Delay(5000).ContinueWith(_ => Machine.Fire(Event.Start));
            })
            .Ignore(Event.ConnectionLost)
            .Permit(Event.Start, State.Connecting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Faulted)
            .OnEntry(_ =>
            {
                Tracker.IncrementError("Device Faulted");
                OnDeviceFaultedAsync();
            })
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.NoActivity)
            .OnEntry(_ => { OnNoActivityAsync(); })
            .Permit(Event.Stop, State.Offline)
            .Permit(Event.ConnectRetry, State.Connecting);

        Machine.Configure(State.Stopping)
            .OnEntry(_ => OnDeviceStoppingAsync())
            .Permit(Event.Stop, State.Offline);

        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            var ex = new InvalidOperationException($"Invalid transition: {trigger} in {state}");
            OnError("StateMachine_LogicGap", ex);
        });
    }


    private async void ExecuteConnect()
    {
        try
        {
            // ConnectAsync is the abstract/virtual method in your derived class
            bool success = await ConnectAsync();

            if (success)
                Machine.Fire(Event.ConnectSuccess);
            else
                Machine.Fire(Event.ConnectFailed);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Connection exception", Config.Name);
            Machine.Fire(Event.ConnectFailed);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            await SendHeartbeatAsync(ct);
            _lastResponse = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_interval, ct);
                if (Machine.State == State.Connected)
                {
                    // 1. Check for timeout (Active Heartbeat) - Skip if _timeout is 0 or less
                    if (_timeout > 0)
                    {
                        var timeSinceLastResponse = (DateTime.UtcNow - _lastResponse).TotalMilliseconds;
                        if (timeSinceLastResponse > _timeout)
                        {
                            Logger.Error("[{Dev}] Heartbeat timeout! No response for {ms}ms", Config.Name,
                                timeSinceLastResponse);
                            await Machine.FireAsync(Event.ConnectionLost);
                            break;
                        }
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
        catch (OperationCanceledException)
        {
            await Machine.FireAsync(Event.Stop);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Heartbeat send failure.", Config.Name);
            await Machine.FireAsync(Event.ConnectionLost);
        }
    }

    /// <summary>
    /// Derived classes must call this method when a valid heartbeat response is detected 
    /// from the remote server or PLC.
    /// </summary>
    protected Task NotifyHeartbeatReceived(object s, object s1)
    {
        _lastResponse = DateTime.UtcNow;
        Tracker.HeartBeat(); // Toggles visual H/B on dashboard
        UpdateAndNotify();
        return Task.CompletedTask;
    }
    // --- Core Lifecycle Overrides ---

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. GUARD CLAUSE: If we are already running or trying to connect, just ignore it!
        if (Machine.State != State.Offline &&
            Machine.State != State.Faulted &&
            Machine.State != State.ServerOffline)
        {
            Logger.Warning("Device is already active or starting. Ignoring redundant Start request.");
            return;
        }

        // 2. Only log and fire the event if we are actually starting from a dead state
        Logger.LogInfo("WCS Service Starting...");
        await Machine.FireAsync(Event.Start);
        await OnStartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken token)
    {
        Logger.Information("[{Dev}] Shutting down gracefully...", Config.Name);
        await Machine.FireAsync(Event.Stop);
    }

    public override void OnError(string context, Exception? ex = null)
    {
        var errorMsg = $"[{Config.Name}] Error in {context}. State: {Machine.State}";
        if (ex != null) errorMsg += $", {ex.Message}";
        Logger.Error(ex, errorMsg);
        Tracker.IncrementError(errorMsg);
        UpdateAndNotify();
        if (Machine.CanFire(Event.Error)) Machine.Fire(Event.Error);
    }

    protected override DeviceHealth MapStateToHealth(State state)
    {
        return state switch
        {
            State.Connected => DeviceHealth.Normal,
            State.Unavailable => DeviceHealth.Warning,
            State.Offline => DeviceHealth.Warning,
            State.Connecting => DeviceHealth.Warning,
            State.ServerOffline => DeviceHealth.Warning,
            State.Faulted => DeviceHealth.Critical,
            _ => DeviceHealth.Warning
        };
    }
}