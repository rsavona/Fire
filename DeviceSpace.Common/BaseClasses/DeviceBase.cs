
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Stateless;
using Stateless.Graph;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;
public abstract class DeviceBase<TState, TEvent, TMetric> : IDevice
    where TState : struct, Enum
    where TEvent : struct, Enum
    where TMetric : struct, Enum
{
    // --- Core Dependencies ---
    protected readonly StateMachine<TState, TEvent> Machine;
    public IDeviceConfig Config { get; }
    public readonly DeviceStatusTracker<TState, TEvent> Tracker;
    protected readonly IFireLogger Logger;
    protected LoggingLevelSwitch LogSwitch;
    public bool NeedsHeartbeat { get; set; }
    private bool _disposed = false;
    private string _lastError;
   

    // --- Resource Tracking ---
    protected readonly List<Task> ManagedTasks = [];
    protected readonly List<object> ManagedContainers = [];
    public int ActiveTaskCount => ManagedTasks.Count(t => !t.IsCompleted);
    public int ContainerCount => ManagedContainers.Count;

    public int GetDeepCount()
    {
        int count = 0;
        foreach (var container in ManagedContainers)
        {
            if (container is System.Collections.ICollection collection)
            {
                count += collection.Count;
            }
            else
            {
                // Try reflection for a "Count" or "Length" property if it doesn't implement ICollection
                var type = container.GetType();
                var countProp = type.GetProperty("Count") ?? type.GetProperty("Length") ?? type.GetProperty("ConnectedClientCount");
                if (countProp != null)
                {
                    try
                    {
                        var val = countProp.GetValue(container);
                        if (val is int i) count += i;
                        else if (val is long l) count += (int)l;
                    }
                    catch { /* Ignore reflection errors */ }
                }
            }
        }
        return count;
    }

    // --- IDevice Properties ---
    public string CurrentStateAsString => Machine.State.ToString();
    public DeviceKey Key { get; }
    public event Action<IDevice, IDeviceStatus>? StatusUpdated;

    public event Action<IDevice>? DeviceReady;

    // --- Abstract Methods ---
    private CancellationTokenSource? _sessionCts;

    // A helper to safely provide a token to the derived class
    protected CancellationToken ConnectionToken => _sessionCts?.Token ?? CancellationToken.None;

    protected abstract void ConfigureStateMachine();
    public abstract Task StartAsync(CancellationToken token);
    public abstract Task StopAsync(CancellationToken token);
    protected abstract DeviceHealth MapStateToHealth(TState state);

    protected virtual void OnDeviceReady() => DeviceReady?.Invoke(this);

    protected virtual Task OnStartAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IFireLogger GetLogger()
    {
        return Logger;
    }

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    /// <param name="logLvl"></param>
    /// <param name="statDef"></param>
    /// <param name="eventDef"></param>
    protected DeviceBase(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch logLvl, TState statDef,
        TEvent eventDef)
    {
        Config = config;
        _lastError = "";
        Logger = logger.WithContext("DeviceName", config.Name);
         
        Logger.Information("[{Device}] ------------- Initializing -------------- DeviceBase Constructor", Config.Name);
        LogSwitch = new LoggingLevelSwitch();

    
        LogSwitch.MinimumLevel = LogEventLevel.Verbose;
        Key = new DeviceKey("SYS", config.Name);
        Tracker = new DeviceStatusTracker<TState, TEvent>(statDef, eventDef);
        Tracker.ScreenIndex = config.ScreenIndex;

        Machine = new StateMachine<TState, TEvent>(statDef);

        Machine.OnTransitioned(OnStateChange);

       
    }

    public void StartTransaction(string transactionId,  int idx)
    {
        Tracker.StartTransaction(transactionId, idx);
        Logger.Verbose("[{Device}] Timer started {strId}-{id}", Config.Name, transactionId, idx);
    }
    
    public double StopTransaction(string transactionId, int idx)
    {
        Logger.Verbose("[{Device}] Timer stopped {strId}-{id}", Config.Name, transactionId, idx);
        return Tracker.StopTransaction(transactionId, idx);
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
        Logger.LogInfo("[{Device}] Transition:{Source} -> {Dest} (Trigger: {Trigger})",
            Config.Name, transition.Source, transition.Destination, transition.Trigger);

        if (Tracker.Update(transition.Destination, transition.Trigger, MapStateToHealth(transition.Destination),
            $"Event: {transition.Trigger}"))
        {
            StatusUpdated?.Invoke(this, CreateStatusSnapshot());
        }
    }

    /// <summary>
    /// Prepares a new session token for the device.
    /// </summary>
    /// <param name="globalToken"></param>
    /// <returns></returns>
    protected CancellationToken PrepareSessionToken(CancellationToken globalToken)
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        return _sessionCts.Token;
    }

    public virtual void CancelSession()
    {
        _sessionCts?.Cancel();
        Logger.Information("[{Device}] Session cancellation requested.", Config.Name);
    }


    /// <summary>
    /// Updates the device status and notifies subscribers if a status update occurs.
    /// </summary>
    /// <param name="log">
    /// logging the heartbeat is too much
    /// </param>
    protected void UpdateAndNotify(string comment = "")
    {
      StatusUpdated?.Invoke(this, CreateStatusSnapshot(comment));
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
        graphText = graphText.Replace($"{currentState} [", $"{currentState} [color=gold, penwidth=4, ");

        return graphText;
    }


    /// <summary>
    /// Creates a snapshot of the current device status, including state, health, and various metrics.
    /// </summary>
    /// <returns>A representation of the current device status as an <see cref="IDeviceStatus"/> object.</returns>
    public IDeviceStatus CreateStatusSnapshot( string comment = "")
    {
        return Tracker.ToStatusMessage(this.Key, comment, ActiveTaskCount, ContainerCount, GetDeepCount());
    }

    // =========================================================================================
    // DISPOSAL REGION (Implements both IDisposable and IAsyncDisposable seamlessly)
    // =========================================================================================

    /// <summary>
    /// Standard Synchronous Disposal. 
    /// </summary>
    public void Dispose()
    {
        CancelSession();
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// NEW: Asynchronous Disposal (Required by updated IDevice).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        CancelSession();

        // 1. Perform the async cleanup (Closing network streams, stopping loops)
        await DisposeAsyncCore().ConfigureAwait(false);

        // 2. Perform any remaining synchronous cleanup
        Dispose(false);

        // 3. Prevent the garbage collector from running the finalizer
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Derived classes (like ClientDeviceBase) override this to safely 
    /// await the shutdown of their network sockets and background tasks.
    /// </summary>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        // Fallback: If a child class doesn't override this, run the old sync cleanup method.
        DisposeManagedResources();
        await Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Logger.Debug("[{Device}] Disposing Managed Resources...", Config.Name);
            // This is kept for backwards compatibility with any older sync-only devices
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
        foreach (var container in ManagedContainers)
        {
            if (container is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[{Device}] Error disposing container", Config.Name);
                }
            }
        }
        ManagedContainers.Clear();
        ManagedTasks.Clear();
    }

    protected void RegisterTask(Task task)
    {
        ManagedTasks.Add(task);
        PruneTasks();
    }

    protected void RegisterContainer(object container)
    {
        ManagedContainers.Add(container);
    }

    protected void PruneTasks()
    {
        ManagedTasks.RemoveAll(t => t.IsCompleted);
    }

    /// <summary>
    /// Finalizer.
    /// </summary>
    ~DeviceBase()
    {
        Dispose(false);
    }
    // =========================================================================================


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
            Logger.Error(ex, "[{Dev}] Error in context: {Context}. Current State: {State}",
                Config.Name, context, Machine.State);
            Tracker.IncrementError(message);
            UpdateAndNotify();
        }
    }

    protected void UpdateStatus(TState state, TEvent evt, DeviceHealth health, string comment)
    {
        Tracker.Update(state, evt, health, comment);
        var snapshot = Tracker.ToStatusMessage(Key);
        StatusUpdated?.Invoke(this, snapshot);
    }

    public virtual IEnumerable<DiagCommand> GetAvailableCommands()
    {
        yield return new DiagCommand("ResetTracker", "Clears all RX/TX counters");
        yield return new DiagCommand("GetState", "Returns current State Machine status");
    }

    public virtual async Task<DiagResult> ExecuteCommandAsync(string name, Dictionary<string, string> parameters)
    {
        return DiagResult.Fail("Unknown command");
    }
}