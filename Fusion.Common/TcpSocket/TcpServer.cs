using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;
using Fusion.Common.Contracts;

namespace Fusion.Common.TcpSocket
{
    public enum TcpListenerState
    {
        Stopped,
        Starting,
        Listening,
        FailedAddressInUse,
        FailedRetrying,
        Exception
    }

    public class TcpServer : IDisposable
    {
        private const int RETRY_DELAY_SECONDS = 30;

        private readonly int _maxBufferSize;
        private readonly int _listenPort;
        private readonly IMessageProcessor _messageProcessor;
        private readonly ConcurrentDictionary<string, ClientConnection> _connectedClients = new();
        private readonly IFireLogger _logger;
        public ITerminationStrategy? TerminationStrategy;
        private TcpListener? _listener;
        private CancellationTokenSource? _serverCts;
        private readonly int _timeoutMs;

        // ---  EVENTS ---
        public event Action<TcpListenerState>? ListenerStateChanged;
        public event Action<string, bool, TcpClient?>? ClientConnectionChanged;
        public event Action<string, Exception?>? ServerError;

        private record ClientConnection(
            TcpClient Client,
            CancellationTokenSource Cts,
            Task ClientTask,
            DateTime LastSeen);

        public TcpServer(
            int listenPort,
            IMessageProcessor messageProcessor,
            IFireLogger logger,
            ITerminationStrategy? termStrat = null,
            int timeoutMs = -1,
            int maxBufferSize = 65535)
        {
            // Assign the default strategy if none was provided
            _listenPort = listenPort;
            _messageProcessor = messageProcessor;
            _logger = logger;
            _maxBufferSize = maxBufferSize;
            TerminationStrategy = termStrat;
            _timeoutMs = timeoutMs;

            _logger.Information("[Server:{Port}] Initialized with Strategy: {Strategy}. Max Buffer: {MaxBuffer}",
                _listenPort, TerminationStrategy?.GetType().Name, _maxBufferSize);
        }

        private async Task StartSocketWatchdogAsync(CancellationToken token)
        {
            _logger.Information("[Server:{Port}] Heartbeat Watchdog started.", _listenPort);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    var now = DateTime.UtcNow;

                    if (_timeoutMs > 0)
                    {
                        var timeout = TimeSpan.FromMilliseconds(_timeoutMs);
                        foreach (var kvp in _connectedClients)
                        {
                            if (now - kvp.Value.LastSeen > timeout)
                            {
                                // Test the actual TCP socket instead of ICMP Ping
                                if (IsSocketAlive(kvp.Value.Client))
                                {
                                    _logger.Information("[{ClientKey}] Watchdog: No messages, but TCP connection is still active. Keeping connection alive.", kvp.Key);
                                    _connectedClients[kvp.Key] = kvp.Value with { LastSeen = DateTime.UtcNow };
                                    continue;
                                }

                                _logger.Warning("[{ClientKey}] Watchdog: No heartbeat detected and TCP socket is dead. Terminating.", kvp.Key);
                                _ = CleanupClient(kvp.Key);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    NotifyError("Watchdog Error", ex);
                }
            }
        }

        private bool IsSocketAlive(TcpClient client)
        {
            try
            {
                if (client == null || !client.Connected || client.Client == null)
                    return false;

                // Check if the socket has been closed gracefully by the remote host
                if (client.Client.Poll(0, SelectMode.SelectRead))
                {
                    byte[] checkConn = new byte[1];
                    if (client.Client.Receive(checkConn, SocketFlags.Peek) == 0)
                    {
                        return false; // Connection closed
                    }
                }

                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        public int ConnectedClientCount => _connectedClients.Count;

        public List<string> GetConnectedClients() => _connectedClients.Keys.ToList();

        private async Task ListenForClientDataAsync(TcpClient client, string clientKey, CancellationToken token)
        {
            _logger.Debug("[{ClientKey}] Read loop (Pipelines) started.", clientKey);
            
            var stream = client.GetStream();
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: _maxBufferSize));

            try
            {
                while (client.Connected && !token.IsCancellationRequested)
                {
                    ReadResult result = await reader.ReadAsync(token);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    while (true)
                    {
                        // DEEP TRACE: Print the current raw buffer content being evaluated
                        var rawTrace = Encoding.ASCII.GetString(buffer.ToArray());
                        _logger.Verbose("[{ClientKey}] DEEP TRACE | Raw Buffer Evaluation ({Length} bytes): {Raw}", clientKey, buffer.Length, rawTrace.Replace("\r", "\\r").Replace("\n", "\\n"));

                        var position = TerminationStrategy?.FindTerminator(buffer);

                        if (position != null)
                        {
                            // Found a message
                            var message = buffer.Slice(0, position.Value);
                            
                            _logger.Verbose("[{ClientKey}] Message Detected. Size: {Size}", clientKey, message.Length);

                            if (_connectedClients.TryGetValue(clientKey, out var conn))
                            {
                                _connectedClients[clientKey] = conn with { LastSeen = DateTime.UtcNow };
                            }

                            // Process message
                            var success = await _messageProcessor.ProcessMessageAsync(
                                message, 
                                clientKey, 
                                (payload) => SendResponseAsync(string.Empty, clientKey, payload, token),
                                token);

                            if (success)
                            {
                                // Advance the buffer past the message
                                buffer = buffer.Slice(position.Value);
                            }
                            else
                            {
                                _logger.Warning("[{ClientKey}] Processor returned failure. Disconnecting.", clientKey);
                                DisconnectClient(clientKey);
                                return;
                            }
                        }
                        else
                        {
                            // No more complete messages in the current buffer
                            break;
                        }
                    }

                    // Tell the PipeReader how much of the buffer has been consumed and how much has been examined.
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (result.IsCompleted)
                    {
                        _logger.Information("[{ClientKey}] PipeReader completed.", clientKey);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("[{ClientKey}] Read loop canceled gracefully.", clientKey);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{ClientKey}] Error in Pipeline read loop.", clientKey);
                NotifyError($"Error on connection {clientKey}", ex);
                DisconnectClient(clientKey);
            }
            finally
            {
                await reader.CompleteAsync();
                _logger.Debug("[{ClientKey}] Pipeline reader completed and stream closed.", clientKey);
            }
        }

        public async Task<bool> SendResponseAsync(string elementName, string clientKey, object payload,
            CancellationToken token = default)
        {
            if (payload is not string msg )
            {
                NotifyError($"Failed to send in SendResponseAsync.  Wrong Payload ");
                _logger.Error("Bad Payload"); return false;
                
            }
            if (!_connectedClients.TryGetValue(clientKey, out var connection))
            {
                NotifyError($"Client Not Found ");
                _logger.Warning("[{ClientKey}] Send failed: Client not found.", clientKey);
                return false;
            }

            try
            {
                byte[] bytesToSend = Encoding.ASCII.GetBytes(msg);
                await connection.Client.GetStream().WriteAsync(bytesToSend, token);

                // Logging and Event in Local Time
                _logger.Information("[{ClientKey}] Sent: {msg}", clientKey, msg);
                return true;
            }
            catch (Exception ex)
            {
                NotifyError($"Failed to send to {clientKey}", ex);
                await CleanupClient(clientKey);
                return false;
            }
        }

        // ---  Boilerplate / Management Methods ---

        private void HandleNewClient(TcpClient client, CancellationToken token)
        {
            if (client.Client.RemoteEndPoint is IPEndPoint remoteIpEndPoint)
            {
                string clientKey = $"{remoteIpEndPoint.Address}:{remoteIpEndPoint.Port}";
                _logger.Information("[Server:{Port}] New connection from {ClientKey}", _listenPort, clientKey);

                var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task clientTask = Task.Run(
                    () => ListenForClientDataAsync(client, clientKey, clientCts.Token),
                    clientCts.Token);

                var connection = new ClientConnection(client, clientCts, clientTask, DateTime.UtcNow);

                if (_connectedClients.TryAdd(clientKey, connection))
                {
                    ClientConnectionChanged?.Invoke(clientKey, true, client);
                }
                else
                {
                    _logger.Warning("[Server:{Port}] ClientKey {ClientKey} already exists.", _listenPort, clientKey);
                    clientCts.Cancel();
                    clientCts.Dispose();
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _logger.Information("[Server:{Port}] Starting TCP Server...", _listenPort);
            SetListenerState(TcpListenerState.Starting);

            try
            {
                while (!_serverCts.Token.IsCancellationRequested)
                {
                    await ListenForClientConnectionsAsync(_serverCts.Token);
                }
            }
            finally
            {
                SetListenerState(TcpListenerState.Stopped);
            }
        }

        private async Task ListenForClientConnectionsAsync(CancellationToken token)
        {
            int retryCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, _listenPort);
                    _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _listener.Start();
                    SetListenerState(TcpListenerState.Listening);
                    retryCount = 0;

                    while (!token.IsCancellationRequested)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync(token);
                        HandleNewClient(client, token);
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;
                    SetListenerState(TcpListenerState.FailedRetrying);
                    _listener?.Stop();
                    await Task.Delay(TimeSpan.FromSeconds(RETRY_DELAY_SECONDS), token);
                }
            }
        }

        private void SetListenerState(TcpListenerState newState) => ListenerStateChanged?.Invoke(newState);

        private void NotifyError(string context, Exception? ex = null)
        {
            if (ex == null)
            {
                _logger.Error("[Server:{Port}] {Context}: {Message}", _listenPort, context);
                ServerError?.Invoke(context, null);
            }
            else
            {
                _logger.Error(ex, "[Server:{Port}] {Context}: {Message}", _listenPort, context, ex.Message);
                ServerError?.Invoke(context, ex);
            }
        }

        private Task CleanupClient(string key)
        {
            return Task.Run(() =>
            {
                if (_connectedClients.TryRemove(key, out var connection))
                {
                    try
                    {
                        ClientConnectionChanged?.Invoke(key, false, null);
                        connection.Cts.Cancel();
                        if (connection.Client.Connected) connection.Client.Client.Shutdown(SocketShutdown.Both);
                        connection.Client.Close();
                        connection.Client.Dispose();
                        connection.Cts.Dispose();
                    }
                    catch (Exception ex)
                    {
                        NotifyError("Cleanup Error", ex);
                    }
                }
            });
        }

        /// <summary>
        /// Manually terminates a client connection. 
        /// Used by the Watchdog when a heartbeat timeout is detected.
        /// </summary>
        public void DisconnectClient(string key)
        {
            _logger.Information("[{ClientKey}] Manual disconnect requested (Watchdog/Timeout).", key);
            _ = CleanupClient(key);
        }

        public void Dispose()
        {
            _serverCts?.Cancel();
            _listener?.Stop();
            foreach (var key in _connectedClients.Keys) _ = CleanupClient(key);
        }

        public async Task StopAsync()
        {
            
            _serverCts?.Cancel();
            _listener?.Stop();
            foreach (var key in _connectedClients.Keys) await CleanupClient(key);
        }
    }
}