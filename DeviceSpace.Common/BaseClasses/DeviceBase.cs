using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Core;
using Stateless;
using Stateless.Graph;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;

public abstract class DeviceBase<TState, TEvent, TMetric> : IDevice, IDiagnosticProvider
    where TState : struct, Enum
    where TEvent : struct, Enum
    where TMetric : struct, Enum
{
    // --- Core Dependencies ---
    protected readonly StateMachine<TState, TEvent>         Machine;
    public             IDeviceConfig                        Config { get; }
    protected readonly DeviceStatusTracker<TState, TEvent>  Tracker;
    protected readonly ILogger                              Logger;
    protected LoggingLevelSwitch                            LogSwitch;
    public bool                                             NeedsHeartbeat { get; set; }
    private bool                                            _disposed = false;

    // --- IDevice Properties ---
    public string CurrentStateAsString => Machine.State.ToString();
    public DeviceKey Key { get; }
    public event Action<IDevice, IDeviceStatus>? StatusUpdated;
    public event Action<IDevice>? DeviceReady;
    // --- Abstract Methods ---
    protected abstract void ConfigureStateMachine();
    public abstract Task StartAsync(CancellationToken token);
    public abstract Task StopAsync(CancellationToken token);
    protected abstract DeviceHealth MapStateToHealth(TState state);

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    /// <param name="logLvl"></param>
    /// <param name="statDef"></param>
    /// <param name="eventDef"></param>
    protected DeviceBase(IDeviceConfig config, ILogger logger, LoggingLevelSwitch logLvl,  TState statDef, TEvent eventDef)
    {
        Config = config;
        Logger = logger.ForContext("DeviceName", config.Name);

        LogSwitch = logLvl;
        Key = new DeviceKey("SYS", config.Name);
        Tracker = new DeviceStatusTracker<TState, TEvent>(statDef, eventDef);

        Machine = new StateMachine<TState, TEvent>(statDef);

        Machine.OnTransitioned(OnStateChange);

        // Log initialization
        Logger.Debug("[{Device}] Initializing DeviceBase. Initial State: {State}", Config.Name, statDef);
    }

    public string GetDeviceVersion()
    {
  
        // Pulls the version from the actual DLL (e.g., 2.5.0.0)
        return GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

   
    protected virtual void ConfigureGlobalErrorHandling(TEvent errorTrigger)
    {
        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            var msg = $"Invalid transition: Trigger '{trigger}' is not allowed in state '{state}'";
            OnError("StateMachine_LogicGap", new InvalidOperationException(msg));

            if (Machine.CanFire(errorTrigger))
                Machine.Fire(errorTrigger);
        });
    }


    /// <summary>
    /// Asynchronously updates the event status by triggering a status update
    /// and notifying any registered listeners. This method is typically
    /// used to ensure that the device's current status is accurately reflected
    /// to external systems or listeners.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected async Task EventUpdateAsync()
    {
        await Task.Run(() =>
        {
            StatusUpdated?.Invoke(this, CreateStatusSnapshot());
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Triggered automatically by the State Machine whenever state changes.
    /// </summary>
    protected virtual void OnStateChange(StateMachine<TState, TEvent>.Transition transition)
    {
        using (LogContext.PushProperty("DeviceName", Config.Name))

            // LOGGING: extensive info on every state change
            Logger.Debug("[{Device}] Transition:{Source} -> {Dest} (Trigger: {Trigger})",
                Config.Name, transition.Source, transition.Destination, transition.Trigger);

        Tracker.Update(transition.Destination, transition.Trigger, MapStateToHealth(transition.Destination),
            $"Event: {transition.Trigger}");
        StatusUpdated?.Invoke(this, CreateStatusSnapshot());
    }


    /// <summary>
    /// Updates the device status and notifies subscribers if a status update occurs.
    /// </summary>
    /// <param name="log">
    /// logging the heartbeat is too much
    /// </param>
    protected void UpdateAndNotify(bool log = true)
    {
        Logger.Verbose("[{Dev}] Stats: In={In}, Out={Out}, Err={Err}",
            Config.Name, Tracker.CountInbound, Tracker.CountOutbound, Tracker.CountError);
        StatusUpdated?.Invoke(this, CreateStatusSnapshot());
    }

    /// <summary>
    /// Exports the current state machine representation to a Graphviz-compatible format,
    /// with additional visual customization applied to specific states such as Offline, Connected, Faulted,
    /// and Processing, and the current state highlighted.
    /// </summary>
    /// <returns>A string containing the Graphviz representation of the state machine with applied visual customizations.</returns>
    public string ExportToGraphviz()
    {
        string graphText = UmlDotGraph.Format(Machine.GetInfo());

        graphText = graphText.Replace("Offline [", "Offline [fillcolor=lightgrey, style=filled, ");
        graphText = graphText.Replace("Connected [", "Connected [fillcolor=green, style=filled, ");
        graphText = graphText.Replace("Faulted [", "Faulted [fillcolor=red, style=filled, fontcolor=white, ");
        graphText = graphText.Replace("Processing [", "Processing [fillcolor=orange, style=filled, ");

        string currentState = Machine.State.ToString();
        // Add a double-circle or a bright yellow highlight to the current state
        graphText = graphText.Replace($"{currentState} [", $"{currentState} [color=gold, penwidth=4, ");

        return graphText;
    }


    /// <summary>
    /// Creates a snapshot of the current device status, including state, health, and various metrics.
    /// </summary>
    /// <returns>A representation of the current device status as an <see cref="IDeviceStatus"/> object.</returns>
    public IDeviceStatus CreateStatusSnapshot()
    {
        return Tracker.ToStatusMessage(this.Key);
    }


    /// <summary>
    /// Disposes the resources used by the instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Logger.Debug("[{Device}] Disposing Managed Resources...", Config.Name);
            DisposeManagedResources();
        }

        _disposed = true;
    }

    /// <summary>
    /// Releases managed resources specific to the device.
    /// This method is called during the disposal process to clean up
    /// resources created and managed by the derived device implementation.
    /// </summary>
    protected virtual void DisposeManagedResources()
    {
    }

    /// <summary>
    /// Finalizer.
    /// </summary>
    ~DeviceBase()
    {
        Dispose(false);
    }


    /// <summary>
    /// Helper for OnEntry(t => ...)
    /// </summary>
    protected void UpdateTracker(StateMachine<TState, TEvent>.Transition t, string message)
    {
        UpdateTracker(t.Destination, t.Trigger, message);
    }

    /// <summary>
    /// Auto-calculates Health based on State and logs appropriately.
    /// </summary>
    protected void UpdateTracker(TState newState, TEvent lastEvent, string message)
    {
        DeviceHealth newHealth = MapStateToHealth(newState);
        UpdateTracker(newState, lastEvent, newHealth, message);
    }

    /// <summary>
    /// Updates internal tracker and logs to Serilog based on Health Severity.
    /// </summary>
    protected void UpdateTracker(TState newState, TEvent lastEvent, DeviceHealth health, string message)
    {
        using (LogContext.PushProperty("DeviceName", Config.Name))
        {
            switch (health)
            {
                case DeviceHealth.Critical:
                case DeviceHealth.Error:
                    Logger.Error("[{Device}] CRITICAL UPDATE: State={State} | {Msg}", Config.Name, newState, message);
                    break;

                case DeviceHealth.Warning:
                    Logger.Warning("[{Device}] WARNING: State={State} | {Msg}", Config.Name, newState, message);
                    break;

                default:
                    // For normal operations, use Debug to keep logs clean, or Info if it's a state change
                    // Since OnStateChange already logs transitions as Info, we can keep this as Debug 
                    // unless it's a specific status message update.
                    Logger.Debug("[{Device}] Status Update: State={State} | {Msg}", Config.Name, newState, message);
                    break;
            }
        }
    }


    public virtual void OnError(string context, Exception? ex = null)
    {
        using (LogContext.PushProperty("DeviceName", Config.Name))
        {
            var message = context;
            if (ex != null) message += ": " + ex.Message;  
          
            Logger.Error(ex, "[{Dev}] Operation failed: {Msg}", Config.Name, message);
            Logger.Error(ex, "[{Dev}] Error in context: {Context}. Current State: {State}",
                Config.Name, context, Machine.State);
            
            Tracker.IncrementError(message);
            UpdateAndNotify();
        }
    }

    protected void UpdateStatus(TState state, TEvent evt, DeviceHealth health, string comment)
    {
        // 1. Update the internal tracker
        Tracker.Update(state, evt, health, comment);

        // 2. Create the snapshot
        var snapshot = Tracker.ToStatusMessage(Key);

        // 3. Raise the event that the Manager is subscribed to
        StatusUpdated?.Invoke(this, snapshot);
    }

    public virtual IEnumerable<DiagCommand> GetAvailableCommands()
    {
        // Every device has these
        yield return new DiagCommand("ResetTracker", "Clears all RX/TX counters");
        yield return new DiagCommand("GetState", "Returns current State Machine status");
    }

    public virtual async Task<DiagResult> ExecuteCommandAsync(string name, Dictionary<string, string> parameters)
    {
        return DiagResult.Fail("Unknown command");
    }
}