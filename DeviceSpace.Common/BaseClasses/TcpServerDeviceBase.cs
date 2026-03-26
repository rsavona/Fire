using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Timers;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Stateless;
using Serilog;
using Serilog.Core;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;

public abstract class TcpServerDeviceBase<TProcessor>
    : DeviceBase<TcpServerDeviceBase<TProcessor>.State, TcpServerDeviceBase<TProcessor>.Event, DeviceMetric>
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
        ServerError
    }

    protected readonly TcpServer Server;
    protected readonly TProcessor Processor;
    protected readonly int Port;
    protected readonly int MaxClients;
    protected readonly System.Timers.Timer Watchdog;
    protected readonly int HeartbeatTimeoutMs;
    protected readonly ConcurrentDictionary<string, DateTime> ConnectedClients = new();

    private StateMachine<State, Event>.TriggerWithParameters<string>? _clientDisconnectedTrigger;

    
    protected virtual void OnSingleClientConnected()
    {
    }

    protected virtual void OnMultiClientConnected()
    {
    }

    protected virtual void OnClientDisconnected(string client, int remaining )
    {
        
    }

    protected TcpServerDeviceBase(IDeviceConfig config, IFireLogger logger, TProcessor processor,
        LoggingLevelSwitch swtch, int port, ITerminationStrategy terminalStr, int maxClients = 1)
        : base(config, logger, swtch, State.Offline, Event.Start)
    {
        Processor = processor;
        Port = port;
        MaxClients = maxClients;

        HeartbeatTimeoutMs = config.Properties.TryGetValue("HeartbeatTimeoutMs", out var ms)
            ? Convert.ToInt32(ms)
            : 30000;
        Watchdog = new System.Timers.Timer(1000) { AutoReset = true };
        Watchdog.Elapsed += OnWatchdogScan;

        Server = new TcpServer(Port, Processor, logger,  terminalStr, 2000);

        Server.ListenerStateChanged += OnServerListenerStateChanged;
        Server.ClientConnectionChanged += OnInternalConnectionChanged;

        Processor.HeartbeatReceived += OnProcessorHeartbeatReceived;
        ConfigureStateMachine();
    }

    protected sealed override void ConfigureStateMachine()
    {
        _clientDisconnectedTrigger = Machine.SetTriggerParameters<string>(Event.ClientDisconnected);

        // --- OFFLINE ---
        Machine.Configure(State.Offline)
            .OnEntry(OnEnterOffline)
            .Permit(Event.Start, State.Starting);

        // --- STARTING ---
        Machine.Configure(State.Starting)
            .OnEntryAsync(OnEnterStartingAsync)
            .Permit(Event.ServerStarted, State.Listening)
            .Permit(Event.ServerFailed, State.Faulted)
            .Permit(Event.ServerError, State.Faulted);

        // --- LISTENING ---
        Machine.Configure(State.Listening)
            .OnEntry(() => Watchdog.Start())
            .Permit(Event.ClientConnected, State.Connected)
            .Permit(Event.Stop, State.Stopping) // Handle Stop while waiting for clients
            .Permit(Event.ServerError, State.Faulted);

        if (MaxClients > 1)
        {
            // --- CONNECTED ---
            Machine.Configure(State.Connected)
                .SubstateOf(State.Listening)
                .OnEntry(OnMultiClientConnected)
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
                .PermitDynamic(_clientDisconnectedTrigger, ClientDisconnected)
                // Allow a Stop event to jump straight to Stopping from Connected
                .Permit(Event.Stop, State.Stopping)
                .Permit(Event.ServerError, State.Faulted);
        }
        else
        {
             Machine.Configure(State.Connected)
                 .OnEntry(OnSingleClientConnected)
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
                .PermitDynamic(_clientDisconnectedTrigger, ClientDisconnected)
                // Allow a Stop event to jump straight to Stopping from Connected
                .Permit(Event.Stop, State.Stopping)
                .Permit(Event.ServerError, State.Faulted);
        }

        // --- STOPPING ---
        Machine.Configure(State.Stopping)
            .OnEntryAsync(OnEnterStoppingAsync)
            .Permit(Event.Stop, State.Offline) // Transition to final state once server cleanup is done
            .Permit(Event.ServerError, State.Faulted);

        // --- FAULTED ---
        Machine.Configure(State.Faulted)
            .OnEntry(OnEnterFaulted)
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Stopping);

        ConfigureGlobalErrorHandling(Event.ServerError);
    }
    
    /// <summary>
    /// Orchestrates the device startup sequence.
    /// </summary>
    public override async Task StartAsync(CancellationToken token)
    {
        // Trigger the state machine to move from Offline -> Starting
        await Machine.FireAsync(Event.Start);
        OnStartAsync(token);
        await Task.Delay(-1, token);
    }

     protected override void OnStateChange(StateMachine<State, Event>.Transition transition)
    {
        // 1. Log the transition for local debugging
        Logger.Debug("[{Device}] Transition: {Source} -> {Dest} (Trigger: {Trigger})",
            Config.Name, transition.Source, transition.Destination, transition.Trigger);

        // 2. Build a dynamic comment based on the connection context
        string contextComment = "";


        if (transition.Destination == State.Connected)
        {
            // When connected, show WHO connected and WHERE (IP and Port)
            var clients = Server.GetConnectedClients();
            if  (clients.Count > 1)
                contextComment = $"Connected: {clients.Count}" ;
            else if (clients.Count > 0)
                contextComment = $"Connected: {clients[0]}" ;
        }
        else if (transition.Destination == State.Listening )
        {
            contextComment = $"{transition.Destination.ToString()}: {Port}";
        }
        else
        {
            contextComment = $"{transition.Trigger.ToString()}:  port {Port}";
        }

        Tracker.Update(
            transition.Destination,
            transition.Trigger,
            MapStateToHealth(transition.Destination),
            contextComment);

        UpdateAndNotify();
    }
     
    /// <summary>
    /// Orchestrates the device shutdown sequence.
    /// </summary>
    public override async Task StopAsync(CancellationToken token)
    {
        // Trigger the state machine to move toward Offline
        await Machine.FireAsync(Event.Stop);
    }


    protected virtual void OnEnterOffline()
    {
        Watchdog.Stop();
        ConnectedClients.Clear();
        Tracker.SetConnectionCount(0);
        UpdateAndNotify();
    }

    protected virtual async Task OnEnterStartingAsync()
    {
        try
        {
            // Start the server in a managed background task
            // We use CancellationToken.None here because the server manages its own internal CTS
            _ = Task.Run(async () => { await Server.StartAsync(CancellationToken.None); });

            Logger.Debug("[{Dev}] TCP Server start initiated on port {Port}.", Config.Name, Port);
        }
        catch (Exception ex)
        {
            OnError("Startup_Failure", ex);
            await Machine.FireAsync(Event.ServerFailed);
        }
    }

    protected virtual void OnEnterFaulted()
    {
        Logger.Error("[{Dev}] Device entered FAULTED state. Stopping server.", Config.Name);
        _ = Server.StopAsync();
        UpdateAndNotify();
    }

    protected virtual async Task OnEnterStoppingAsync()
    {
        Logger.Information("[{Dev}] Stopping TCP Server...", Config.Name);

        // 1. Stop the TCP Listener and disconnect all clients
        await Server.StopAsync();

        // 2. Finalize the state machine transition to Offline
        await Machine.FireAsync(Event.Stop);
    }


    // EXPOSED: Accessible to the child class
    public virtual async Task<bool> SendAsync(string clientId, string payload, CancellationToken ct = default)
    {
        if (Machine.State != State.Connected && Machine.State != State.Processing) return false;
        return await Server.SendResponseAsync(Config.Name, clientId, payload, ct);
    }

    public virtual async Task<bool> SendAsync(string payload, CancellationToken ct = default)
    {
        var firstClient = ConnectedClients.Keys.FirstOrDefault();
        return firstClient != null && await SendAsync(firstClient, payload, ct);
    }

    private void OnInternalConnectionChanged(string key, bool connected, TcpClient? client)
    {
        if (connected)
        {
            if (MaxClients == 1)
                foreach (var c in ConnectedClients.Keys)
                    Server.DisconnectClient(c);
            ConnectedClients.TryAdd(key, DateTime.Now);
            Machine.Fire(Event.ClientConnected);
        }
        else Machine.Fire(_clientDisconnectedTrigger!, key);
    }

    protected void UpdateClientTimestamp(string clientId)
    {
        if (ConnectedClients.ContainsKey(clientId)) ConnectedClients[clientId] = DateTime.Now;
    }

    private void OnServerListenerStateChanged(TcpListenerState s)
    {
        if (s == TcpListenerState.Listening) Machine.Fire(Event.ServerStarted);
    }

    private State ClientDisconnected(string clientId)
    {
        ConnectedClients.TryRemove(clientId, out _);
        OnClientDisconnected(clientId, ConnectedClients.Count());
        return ConnectedClients.IsEmpty ? State.Listening : State.Connected;
    }

    protected virtual void OnWatchdogScan(object? sender, ElapsedEventArgs e)
    {
        /* Scan and Server.DisconnectClient if now - value > timeout */
    }

    protected virtual void OnProcessorHeartbeatReceived(string id) => UpdateClientTimestamp(id);

    protected override DeviceHealth MapStateToHealth(State state) => state switch
    {
        State.Connected or State.Processing => DeviceHealth.Normal,
        State.Listening => DeviceHealth.Warning, // Warning because no client is active
        State.Faulted => DeviceHealth.Critical,
        State.Offline or State.Stopping => DeviceHealth.Warning,
        _ => DeviceHealth.Warning
    };
}