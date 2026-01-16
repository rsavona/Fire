using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;

public abstract class TcpServerSingleClientDeviceBase<TProcessor>
    : DeviceBase<TcpServerSingleClientDeviceBase<TProcessor>.State, TcpServerSingleClientDeviceBase<TProcessor>.Event>
    where TProcessor : IMessageProcessor
{
    #region Enums

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
        Error
    }

    #endregion

    protected readonly TcpServer Server;
    protected readonly TProcessor Processor;
    protected readonly int Port;
    protected readonly System.Timers.Timer Watchdog;
    protected readonly int HeartbeatTimeoutMs;
    protected readonly bool IsWatchdogEnabled; // Toggle for optional watchdog

    // Tracking for the single allowed client
    protected TcpClient? _currentClient;
    protected KeyValuePair<string, DateTime>? ActiveClient { get; private set; }
    public DateTime lastSeen { get; set; }

    protected TcpServerSingleClientDeviceBase(IDeviceConfig config, ILogger logger, TProcessor processor, int port)
        : base(config, logger, State.Offline, Event.Start)
    {
        Processor = processor;
        Port = port;

        // 1. Determine if Watchdog is enabled via Config
        IsWatchdogEnabled = config.Properties.TryGetValue("EnableWatchdog", out var enabled) 
                            && Convert.ToBoolean(enabled);

        HeartbeatTimeoutMs = config.Properties.TryGetValue("HeartbeatTimeoutMs", out var ms)
            ? Convert.ToInt32(ms)
            : 10000;

        Watchdog = new System.Timers.Timer(1000) { AutoReset = true };
        Watchdog.Elapsed += OnWatchdogScan;

        Server = new TcpServer(Port, Processor, logger);
        Server.ListenerStateChanged += OnServerListenerStateChanged;
        
        // Ensure your TcpServer implementation passes the TcpClient
        Server.ClientConnectionChanged += OnInternalConnectionChanged;

        ConfigureStateMachine();
    }

    protected abstract Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct);

    protected sealed override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .OnEntry(() =>
            {
                Watchdog.Stop();
                CleanupClient();
                UpdateAndNotify();
            })
            .Permit(Event.Start, State.Starting);

        Machine.Configure(State.Starting)
            .OnEntry(() => _ = Server.StartAsync(CancellationToken.None))
            .Permit(Event.ServerStarted, State.Listening)
            .Permit(Event.ServerFailed, State.Faulted);

        Machine.Configure(State.Listening)
            .OnEntry(() => 
            {
                if (IsWatchdogEnabled) Watchdog.Start();
                Logger.Information("[{Dev}] Server listening on port {Port}. Watchdog Enabled: {Watch}", 
                    Config.Name, Port, IsWatchdogEnabled);
            })
            .Ignore(Event.ServerStarted)
            .Permit(Event.ClientConnected, State.Connected)
            .Permit(Event.Stop, State.Stopping)
            .Permit(Event.Error, State.Faulted);

        Machine.Configure(State.Connected)
            .SubstateOf(State.Listening)
            .OnEntry(() => lastSeen = DateTime.UtcNow)
            .Permit(Event.MessageReceived, State.Processing)
            .Permit(Event.ClientDisconnected, State.Listening)
            .PermitReentry(Event.MessageSent);

        Machine.Configure(State.Processing)
            .SubstateOf(State.Connected)
            .OnEntry(() => lastSeen = DateTime.UtcNow)
            .Permit(Event.Error, State.Connected)
            .Permit(Event.ProcessingComplete, State.Connected);

        Machine.Configure(State.Faulted)
            .OnEntry(() =>
            {
                _ = Server.StopAsync();
                UpdateAndNotify();
            })
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Stopping)
            .OnEntry(() =>
            {
                _ = Server.StopAsync();
                Machine.Fire(Event.Stop);
            })
            .Permit(Event.Stop, State.Offline);

        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            var ex = new InvalidOperationException($"Invalid transition: {trigger} is not allowed in {state}");
            OnError("StateMachine_LogicGap", ex);
        });
    }

    private void OnInternalConnectionChanged(string key, bool connected, TcpClient? client)
    {
        if (connected)
        {
            _currentClient = client;
            ActiveClient = new KeyValuePair<string, DateTime>(key, DateTime.UtcNow);
            lastSeen = DateTime.UtcNow;
            
            Logger.Information("[{Dev}] Client connected: {ClientKey}", Config.Name, key);
            Machine.Fire(Event.ClientConnected);
        }
        else if (ActiveClient?.Key == key)
        {
            Logger.Warning("[{Dev}] Client disconnected: {ClientKey}", Config.Name, key);
            CleanupClient();
            Machine.Fire(Event.ClientDisconnected);
        }
    }

    private void CleanupClient()
    {
        _currentClient?.Close();
        _currentClient = null;
        ActiveClient = null;
    }

    protected virtual void OnWatchdogScan(object? sender, ElapsedEventArgs e)
    {
        if (!IsWatchdogEnabled || !ActiveClient.HasValue) return;

        var idleTime = DateTime.UtcNow - lastSeen;
        if (idleTime.TotalMilliseconds > HeartbeatTimeoutMs)
        {
            Logger.Warning("[{Dev}] Watchdog: Idle timeout for {Client}. Duration: {Idle}ms", 
                Config.Name, ActiveClient.Value.Key, (int)idleTime.TotalMilliseconds);
            
            // Force disconnect of hung client if timeout reached
            CleanupClient();
            Machine.Fire(Event.ClientDisconnected);
        }
    }

    protected NetworkStream? GetStream()
    {
        if (_currentClient != null && _currentClient.Connected)
        {
            try { return _currentClient.GetStream(); }
            catch { return null; }
        }
        return null;
    }

    public async Task SendAsync(string data)
    {
        if (Machine.State != State.Connected && Machine.State != State.Processing)
        {
            Logger.Warning("[{Dev}] Send aborted: No client connected.", Config.Name);
            return;
        }

        try
        {
            var stream = GetStream();
            if (stream != null && stream.CanWrite)
            {
                byte[] buffer = Encoding.ASCII.GetBytes(data);
                await stream.WriteAsync(buffer, 0, buffer.Length);
                await stream.FlushAsync();

                Logger.Verbose("[{Dev}] TX RAW << {Data}", Config.Name, data.Trim());
                
                lastSeen = DateTime.UtcNow; // Refresh activity timestamp
                Tracker.IncrementOutbound();
                await Machine.FireAsync(Event.MessageSent);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Write error. Forcing disconnect.");
            CleanupClient();
            Machine.Fire(Event.ClientDisconnected);
        }
    }

    private void OnServerListenerStateChanged(TcpListenerState s)
    {
        if (s == TcpListenerState.Listening) Machine.FireAsync(Event.ServerStarted);
        else if (s == TcpListenerState.FailedAddressInUse) Machine.Fire(Event.ServerFailed);
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
        State.Listening => DeviceHealth.Warning,
        State.Faulted => DeviceHealth.Critical,
        _ => DeviceHealth.Warning
    };
}