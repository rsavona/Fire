using System.Net.Sockets;
using System.Text;
using System.Buffers;
using Fusion.Common.Configurations;
using Fusion.Common.Enums;
using Fusion.Common.Contracts; // Ensure Enums are available for Machine.State
using Fusion.Common.TCP_Classes;
using Serilog;
using Serilog.Core;
using Stateless;

namespace Fusion.Common.BaseClasses;

public abstract class TcpClientElementBase : ClientElementBase
{
    private TcpClient? _tcpClient;
    private readonly string? _host;
    private readonly int _port;
    private readonly int _maxReceiveBufferSize;
    private readonly ITerminationStrategy? _configuredReceiveTerminationStrategy;
    private NetworkStream? TransportStream { get; set; }

    protected virtual ITerminationStrategy? ReceiveTerminationStrategy => _configuredReceiveTerminationStrategy;

    public TcpClientElementBase(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls, bool needsHb = false)
        : base(bus, config, logger, ls, needsHb)
    {
        _host = ConfigurationLoader.GetRequiredConfig<string>(config.Properties, "IPAddress");
        _port = ConfigurationLoader.GetRequiredConfig<int>(config.Properties, "Port");
        _maxReceiveBufferSize = ConfigurationLoader.GetOptionalConfig(config.Properties, "MaxBufferSize", 65535);
        _configuredReceiveTerminationStrategy = CreateConfiguredReceiveTerminationStrategy(config);
    }

    protected override async void OnElementFaultedAsync(CancellationToken token = default)
    {
        Logger.Error("[{Dev}] Element Faulted. Closing TCP connection to {Host}:{Port}", Config.Name, _host, _port);
        try
        {
            await CloseConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Exception during fault-triggered close.", Config.Name);
        }
    }

    protected override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _tcpClient = new TcpClient();

            // Using a 5-second timeout for the physical connection attempt
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_host == null) return false;
            await _tcpClient.ConnectAsync(_host, _port, cts.Token);

            TransportStream = _tcpClient.GetStream();
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning("[{Dev}] ConnectAsync Exception: {Msg}", Config.Name, ex.Message); // Extensive logging
            _tcpClient?.Close();
            _tcpClient = null;
            return false;
        }
    }

    protected override Task ElementConnectedAsync()
    {
        _ = Task.Run(() => ReadLoopAsync(CancellationToken.None));
        return base.ElementConnectedAsync();
    }

    protected override async Task OnElementStoppingAsync()
    {
        await CloseConnectionAsync();

        if (Machine.CanFire(Event.Stop))
        {
            await Machine.FireAsync(Event.Stop);
        }
    }

    protected override void OnStateChange(StateMachine<State, Event>.Transition transition)
    {
        // 1. Log the transition for local debugging
        Logger.Debug("[{Element}] Transition: {Source} -> {Dest} (Trigger: {Trigger})",
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
            contextComment = $"{transition.Destination.ToString()}: {_host} port {_port}";
        }
        else
        {
            contextComment = $"{transition.Trigger.ToString()}: {_host} port {_port}";
        }

        Tracker.Update(
            transition.Destination,
            transition.Trigger,
            MapStateToHealth(transition.Destination),
            contextComment);

        UpdateAndNotify();
    }


    /// <summary>
    /// Close the TCP connection and dispose of the underlying resources.
    /// </summary>
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

    /// <summary>
    /// Detects if the remote side (PLC/Printer) closed the connection gracefully.
    /// </summary>
    /// <param name="client"></param>
    /// <returns></returns>
    private bool CheckTcpConnection(TcpClient? client)
    {
        if (client == null || !client.Connected || client.Client == null)
        {
            return false;
        }

        try
        {
            // Detect if the remote side (PLC/Printer) closed the connection gracefully
            if (client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                if (client.Client.Receive(buff, SocketFlags.Peek) == 0)
                {
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

    /// <summary>
    /// Returns true if the element is connected and the TCP connection is active.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            bool stateIsActive = Machine.State is State.Connected;
            bool socketIsActive = CheckTcpConnection(this._tcpClient);

            // Update the connection count based on both state and socket health
            Tracker.SetConnectionCount(stateIsActive && socketIsActive ? 1 : 0);

            return stateIsActive && socketIsActive;
        }
    }

    /// <summary>
    ///    
    /// </summary>
    /// <param name="incomingData"></param>
    /// <returns></returns>
    protected abstract Task HandleReceivedDataAsync(string incomingData);


    /// <summary>
    /// Override this method if your element needs sa heasrtbeat or staus check
    /// </summary>
    /// <param name="incomingData"></param>
    /// <returns></returns>
    protected virtual bool IsHeartbeat(string incomingData)
    {
        return false;
    }

    /// <summary>
    /// Read loop for the TCP connection.
    /// </summary>
    /// <param name="ct"></param>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var receiveBuffer = new List<byte>();
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

                var strategy = ReceiveTerminationStrategy;
                if (strategy == null)
                {
                    string incomingData = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    await ProcessReceivedMessageAsync(incomingData, ct);
                    continue;
                }

                for (int i = 0; i < bytesRead; i++)
                {
                    receiveBuffer.Add(buffer[i]);
                }

                if (receiveBuffer.Count > _maxReceiveBufferSize)
                {
                    throw new InvalidDataException(
                        $"Receive buffer exceeded {_maxReceiveBufferSize} bytes without a complete message.");
                }

                await ProcessBufferedMessagesAsync(receiveBuffer, strategy, ct);
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException se &&
                                       se.SocketErrorCode == SocketError.OperationAborted)
        {
            // This is normal. The socket was closed/disposed while a read was pending.
            Logger.Debug("[{Dev}] Socket operation aborted (Connection gracefully closed).", Config.Name);
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException se &&
                                       se.SocketErrorCode == SocketError.ConnectionReset)
        {
            // This happens when the remote host (e.g. Virtual Printer) forcefully closes the connection
            Logger.Warning("[{Dev}] Connection forcibly closed by remote host (ConnectionReset).", Config.Name);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Read loop exception.", Config.Name);
        }
        finally
        {
            Logger.Warning("[{Dev}] Read loop exited. Triggering ConnectionLost.", Config.Name);
            if (Machine.CanFire(Event.ConnectionLost))
                await Machine.FireAsync(Event.ConnectionLost);
            await CloseConnectionAsync();
        }
    }

    private static ITerminationStrategy? CreateConfiguredReceiveTerminationStrategy(IElementBlueprint config)
    {
        if (!config.Properties.TryGetValue("TerminationChar", out var terminatorObj))
        {
            return null;
        }

        var terminator = TcpTextEncoding.DecodeEscapedSequence(terminatorObj?.ToString());
        if (string.IsNullOrEmpty(terminator))
        {
            return null;
        }

        return new SequenceTerminationStrategy(Encoding.ASCII.GetBytes(terminator));
    }

    private async Task ProcessBufferedMessagesAsync(
        List<byte> receiveBuffer,
        ITerminationStrategy strategy,
        CancellationToken ct)
    {
        while (receiveBuffer.Count > 0 && !ct.IsCancellationRequested)
        {
            var sequence = new ReadOnlySequence<byte>(receiveBuffer.ToArray());
            var terminator = strategy.FindTerminator(sequence);
            if (terminator == null)
            {
                return;
            }

            var messageSequence = sequence.Slice(0, terminator.Value);
            string message = Encoding.ASCII.GetString(messageSequence.ToArray());
            int consumed = checked((int)messageSequence.Length);
            receiveBuffer.RemoveRange(0, consumed);

            await ProcessReceivedMessageAsync(message, ct);
        }
    }

    private async Task ProcessReceivedMessageAsync(string incomingData, CancellationToken ct)
    {
        // Immediate Check: If it's a heartbeat, return true/exit immediately
        if (IsHeartbeat(incomingData))
        {
            _ = NotifyHeartbeatReceived("", "");
            return;
        }

        await Machine.FireAsync(Event.MessageReceived);
        await HandleReceivedDataAsync(incomingData);
    }

    /// <summary>
    /// Send a message over the TCP connection.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <param name="fireEvent"></param>
    public override async Task SendAsync(string message, CancellationToken token, bool fireEvent = true)
    {
        if (!IsConnected || TransportStream == null)
        {
            Logger.Warning("[{Dev}] Send aborted: Not Connected.", Config.Name);
            await Machine.FireAsync(Event.ConnectionLost);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] buffer = Encoding.ASCII.GetBytes(message);
            await TransportStream.WriteAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
            await TransportStream.FlushAsync(timeoutCts.Token);

            Logger.Verbose("[{Dev}] TX RAW << {Data}", Config.Name, message.Trim());
            if (fireEvent)
            {
                _ = Machine.FireAsync(Event.MessageSent);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Send Failure. Forcing disconnect.", Config.Name);
            await Machine.FireAsync(Event.ConnectionLost);
            throw;
        }
    }

    /// <summary>
    /// Returns the underlying NetworkStream for the TCP connection.
    /// </summary>
    /// <returns></returns>
    protected NetworkStream? GetStream() => TransportStream;

    /// <summary>
    /// Override this method to return the heartbeat message.
    /// </summary>
    /// <returns></returns>
    protected abstract string GetHeartbeatMessage();

    /// <summary>
    /// Override this method to send the heartbeat message.
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public override Task SendHeartbeatAsync(CancellationToken token = default)
    {
        _ = SendAsync(GetHeartbeatMessage(), token, false);
        Logger.Verbose("[{Dev}] Heartbeat sent.*********", Config.Name);
        return Task.CompletedTask;
    }
}
