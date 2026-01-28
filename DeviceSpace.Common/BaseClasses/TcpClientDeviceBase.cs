using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums; // Ensure Enums are available for Machine.State
using Serilog;
using Stateless;

namespace DeviceSpace.Common.BaseClasses;

public abstract class TcpClientDeviceBase : ClientDeviceBase
{
    private TcpClient? _tcpClient;
    private readonly string _host;
    private readonly int _port;
    private NetworkStream? TransportStream { get; set; }

    public TcpClientDeviceBase(IDeviceConfig config, ILogger logger, bool needsHb = false)
        : base(config, logger, needsHb)
    {
        _host = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "IPAddress");
        _port = ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "Port");
    }

    protected override async void DeviceFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] Device Faulted. Closing TCP connection to {Host}:{Port}", Config.Name, _host, _port);
        try
        {
            await CloseConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "[{Dev}] Exception during fault-triggered close.", Config.Name);
        }
    }

    protected override async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _tcpClient = new TcpClient();

            // Using a 5-second timeout for the physical connection attempt
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _tcpClient.ConnectAsync(_host, _port, cts.Token);

            TransportStream = _tcpClient.GetStream();
            await Machine.FireAsync(Event.ConnectSuccess); // Triggers success logic

            _ = Task.Run(() => ReadLoopAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            Logger.Debug("[{Dev}] ConnectAsync Exception: {Msg}", Config.Name, ex.Message); // Extensive logging
            await Machine.FireAsync(Event.ConnectFailed); // Triggers the 5s wait in Reconnecting
        }
    }

     protected override void OnStateChange(StateMachine<State, Event>.Transition transition)
    {
        // 1. Log the transition for local debugging
        Logger.Debug("[{Device}] Transition: {Source} -> {Dest} (Trigger: {Trigger})",
            Config.Name, transition.Source, transition.Destination, transition.Trigger);

        // 2. Build a dynamic comment based on the connection context
        string contextComment;

        
        if (transition.Destination == State.Connected)
        {
            // When connected, show WHO connected and WHERE (IP and Port)
            contextComment = $"Connected: {_host} port {_port}";
            
        }
        else if (transition.Destination == State.Connecting || transition.Destination == State.ServerOffline)
        {
            contextComment = $" {_host} port {_port}";
        }
        else
        {
            contextComment = $"Event: {transition.Trigger}";
        }

        Tracker.Update(
            transition.Destination,
            transition.Trigger,
            MapStateToHealth(transition.Destination),
            contextComment);

        UpdateAndNotify();
    }

     
    

    protected async Task CloseConnectionAsync()
    {
        Logger.Debug("[{Dev}] Closing TCP resources.", Config.Name);
        _tcpClient?.Close();
        if (TransportStream != null)
        {
            await TransportStream.DisposeAsync();
            TransportStream = null;
        }
    }

    private bool CheckTcpConnection(TcpClient? client)
    {
        if (client == null || !client.Connected)
            return false;

        try
        {
            // Detect if the remote side (PLC/Printer) closed the connection gracefully
            if (client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                if (client.Client.Receive(buff, SocketFlags.Peek) == 0)
                {
                    Tracker.IncrementDisconnects();
                    Logger.Warning("[{Dev}] Peer closed the connection (Zero-byte receive).", Config.Name);
                    return false;
                }
            }
        }
        catch (SocketException ex)
        {
            Logger.Debug("[{Dev}] Socket health check failed: {Msg}", Config.Name, ex.Message);
            return false;
        }

        return true;
    }

    public bool IsConnected
    {
        get
        {
            bool stateIsActive = Machine.State is State.Connected or State.Processing;
            bool socketIsActive = CheckTcpConnection(this._tcpClient);

            return stateIsActive && socketIsActive;
        }
    }

    protected abstract Task HandleReceivedDataAsync(string incomingData);


    protected virtual bool IsHeartbeat(string incomingData)
    {
        return false;
    }
    
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        Logger.Debug("[{Dev}] Starting Read Loop.", Config.Name);

        try
        {
            // The IsConnected check here is critical for detecting drops
            while (!ct.IsCancellationRequested && IsConnected)
            {
                if (TransportStream == null) break;
                int bytesRead = await TransportStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0)
                {
                    Logger.Warning("[{Dev}] Read zero bytes. Peer has disconnected.", Config.Name);
                    break;
                }
                Logger.Verbose("[{Dev}] RX RAW >> {Bytes} bytes", Config.Name, bytesRead);
                string incomingData = Encoding.ASCII.GetString(buffer, 0, bytesRead);
               // Immediate Check: If it's a heartbeat, return true/exit immediately
               if (IsHeartbeat(incomingData))
               {
                   NotifyHeartbeatReceived("", "");
                    continue; 
               }
                   
                
                
                await HandleReceivedDataAsync(incomingData);
                await Machine.FireAsync(Event.DataReceived);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Read loop exception.", Config.Name);
        }
        finally
        {
            Logger.Warning("[{Dev}] Read loop exited. Triggering ConnectionLost.", Config.Name);
            await Machine.FireAsync(Event.ConnectionLost);
            await CloseConnectionAsync();
        }
    }

    
     public override async Task SendAsync(string message, CancellationToken token)
    {
        if (!IsConnected || TransportStream == null)
        {
            Logger.Warning("[{Dev}] Send aborted: Not Connected.", Config.Name);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] buffer = Encoding.ASCII.GetBytes(message);
            await TransportStream.WriteAsync(buffer, 0, buffer.Length);
            await TransportStream.FlushAsync();

            Logger.Verbose("[{Dev}] TX RAW << {Data}", Config.Name, message.Trim());
            await Machine.FireAsync(Event.MessageSent);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Send Failure. Forcing disconnect.", Config.Name);
            await Machine.FireAsync(Event.ConnectionLost);
            throw;
        }
    }

    protected NetworkStream? GetStream() => TransportStream;

    protected abstract string GetHeartbeatMessage();
    
      public override Task SendHeartbeatAsync( CancellationToken token = default)
      {
             _ = SendAsync(GetHeartbeatMessage(), token);
            Logger.Verbose("[{Dev}] Heartbeat sent.*********", Config.Name);
             return Task.CompletedTask; 
      }
}