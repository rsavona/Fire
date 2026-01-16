using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Timers;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using ILogger = Serilog.ILogger;
using Stateless;
using Stateless.Reflection;

namespace DeviceSpace.Common.BaseClasses;

public abstract class TcpServerMultiClientDeviceBase<TProcessor>
    : DeviceBase<TcpServerMultiClientDeviceBase<TProcessor>.State, TcpServerMultiClientDeviceBase<TProcessor>.Event>
    where TProcessor : IMessageProcessor
{
    public enum State
    {
        Offline,
        Starting,
        Listening,
        Connected,
        Processing,
        Faulted,
        Stopping
    }

    public enum Event
    {
        Start,
        Stop,
        ServerStarted,
        ServerFailed,
        MessageReceived,
        MessageSent,
        ClientConnected,
        ClientDisconnected,
        ProcessingComplete,
        RecoverableError,
        FatalError
    }

    protected readonly TcpServer Server;
    protected readonly TProcessor Processor;
    protected readonly int Port;
    protected readonly System.Timers.Timer Watchdog;
    protected readonly int HeartbeatTimeoutMs;
    protected readonly ConcurrentDictionary<string, DateTime> ConnectedClients = new();

    private StateMachine<State, Event>.TriggerWithParameters<string>? _clientDisconnectedTrigger;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    /// <param name="processor"></param>
    /// <param name="port"></param>
    protected TcpServerMultiClientDeviceBase(IDeviceConfig config, ILogger logger, TProcessor processor, int port)
        : base(config, logger, State.Offline, Event.Start)
    {
        Processor = processor;
        Port = port;

        HeartbeatTimeoutMs = config.Properties.TryGetValue("HeartbeatTimeoutMs", out var ms)
            ? Convert.ToInt32(ms)
            : 10000;

        Watchdog = new System.Timers.Timer(HeartbeatTimeoutMs / 2) { AutoReset = true };
        Watchdog.Elapsed += OnWatchdogScan;

        Server = new TcpServer(Port, Processor, logger);
        Server.ListenerStateChanged += OnServerListenerStateChanged;
        Server.ClientConnectionChanged += OnInternalConnectionChanged;
        Processor.HeartbeatReceived += OnProcessorHeartbeatReceived;

        ConfigureStateMachine();
    }

    protected sealed override void ConfigureStateMachine()
    {
        _clientDisconnectedTrigger = Machine.SetTriggerParameters<string>(Event.ClientDisconnected);

        Machine.Configure(State.Offline)
            .OnEntry(OnEnterOffline)
            .Permit(Event.Start, State.Starting);

        Machine.Configure(State.Starting)
            .OnEntryAsync(OnEnterStartingAsync)
            .Permit(Event.ServerStarted, State.Listening)
            .Permit(Event.ServerFailed, State.Faulted)
            .Permit(Event.FatalError, State.Faulted);

        Machine.Configure(State.Listening)
            .OnEntry(() =>
            {
                Watchdog.Start();
               
            })
            .Permit(Event.ClientConnected, State.Connected)
            .Permit(Event.Stop, State.Stopping)
            .Permit(Event.FatalError, State.Faulted);

        Machine.Configure(State.Connected)
            .SubstateOf(State.Listening)
            // Handle messages without leaving state
            //       .InternalTransition(_messageReceivedTrigger, (clientId, t) => OnMessageReceived(clientId))
            // Handle recoverable errors without leaving state
            .InternalTransition(Event.RecoverableError, t => OnRecoverableError())
            // Dynamic check: Go to Listening ONLY if it was the last client
            .PermitDynamic(_clientDisconnectedTrigger, clientId => OnClientDisconnected(clientId))
            .Permit(Event.FatalError, State.Faulted)
            .Ignore(Event.MessageReceived)
            .Ignore(Event.MessageSent);

        Machine.Configure(State.Faulted)
            .OnEntry(OnEnterFaulted)
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Stopping)
            .OnEntryAsync(OnEnterStoppingAsync)
            .Permit(Event.Stop, State.Offline);

        ConfigureGlobalErrorHandling(Event.FatalError);
    }

    #region State Transition Logic

    private void OnEnterOffline()
    {
        Watchdog.Stop();
        ConnectedClients.Clear();
        UpdateAndNotify();
    }

    private async Task OnEnterStartingAsync()
    {
        try
        {
            _ = Task.Run(async () => 
            {
                await Server.StartAsync(CancellationToken.None);
            });
        
            Logger.Debug("[{Dev}] Server start initiated background task.", Config.Name);
        }
        catch (Exception ex)
        {
            OnError("Startup", ex);
            Machine.Fire(Event.ServerFailed);
        }
    }


    private void OnRecoverableError()
    {
        Logger.Warning("[{Dev}] Recoverable error reported. Maintaining connection.", Config.Name);
        UpdateAndNotify();
    }

    private State OnClientDisconnected(string clientId)
    {
        ConnectedClients.TryRemove(clientId, out _);
        bool clientsRemaining = !ConnectedClients.IsEmpty;

        Logger.Information("[{Dev}] Client {Id} disconnected. Remaining: {Count}",
            Config.Name, clientId, ConnectedClients.Count);
        Tracker.SetConnectionCount(ConnectedClients.Count);
        UpdateAndNotify();
        return clientsRemaining ? State.Connected : State.Listening;
    }

    private void OnEnterFaulted()
    {
        _ = Server.StopAsync(); // Fire and forget stop
        UpdateAndNotify();
    }

    private async Task OnEnterStoppingAsync()
    {
        await Server.StopAsync();
        Machine.Fire(Event.Stop);
    }

    #endregion

    private void OnInternalConnectionChanged(string key, bool connected, TcpClient? client = null)
    {
        if (connected)
        {
             ConnectedClients.TryAdd(key, DateTime.UtcNow);
             Machine.Fire(Event.ClientConnected);
        }
        else
        {
            // The logic for removal is handled inside OnClientDisconnected via the trigger
            Machine.Fire(_clientDisconnectedTrigger, key);
        }
    }

    protected void UpdateClientTimestamp(string clientId)
    {
        ConnectedClients[clientId] = DateTime.UtcNow;
        Tracker.HeartBeat();
        Tracker.SetConnectionCount(ConnectedClients.Count);
        UpdateAndNotify(false);
    }

    protected void OnProcessorHeartbeatReceived(string clientId)
    {
        UpdateClientTimestamp(clientId);
        Logger.Verbose("[{Dev}] Heartbeat: {ClientId}", Config.Name, clientId);
    }

    protected virtual void OnWatchdogScan(object? sender, ElapsedEventArgs e)
    {
        var timeout = TimeSpan.FromMilliseconds(HeartbeatTimeoutMs);
        var now = DateTime.UtcNow;

        foreach (var client in ConnectedClients)
        {
            if (now - client.Value > timeout)
            {
                Logger.Warning("[{Dev}] Watchdog timeout: {Id}", Config.Name, client.Key);
                Server.DisconnectClient(client.Key);
            }
        }
    }

    private void OnServerListenerStateChanged(TcpListenerState s)
    {
        if (s == TcpListenerState.Listening) Machine.Fire(Event.ServerStarted);
        else if (s is TcpListenerState.FailedAddressInUse or TcpListenerState.Exception)
            OnError("TCP_Listener", new Exception($"Listener State: {s}"));

    }

    public override async Task<Task> StartAsync(CancellationToken token)
    {
        await Machine.FireAsync(Event.Start);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken token)
    {
        await Machine.FireAsync(Event.Stop);
    }

    protected override DeviceHealth MapStateToHealth(State state) => state switch
    {
        State.Connected or State.Processing => DeviceHealth.Normal,
        State.Faulted => DeviceHealth.Critical,
        _ => DeviceHealth.Warning
    };

    protected override void ConfigureGlobalErrorHandling(Event fatalErrorTrigger)
    {
        // 1. Handle Unexpected Triggers
        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            Logger.Warning("[{Dev}] Invalid Trigger: {Trigger} not allowed in {State}",
                Config.Name, trigger, state);
            Machine.Fire(Event.RecoverableError);
        });

    Machine.OnTransitioned(transition =>
        {
            // Update the health and status in the tracker based on the NEW state
            Tracker.Update(  transition.Destination, transition.Trigger, MapStateToHealth(transition.Destination), 
                ConnectedClients.Count,$"Event: {transition.Trigger}");
       
            Logger.Information("[{Dev}] Transition: {Source} -> {Destination} (Trigger: {Trigger})",
                Config.Name, transition.Source, transition.Destination, transition.Trigger);

            // Notify the UI/Bus that the device state has changed
            UpdateAndNotify();
        });
    }
}