using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.Contracts;
using Serilog;
using System.Buffers;

namespace DeviceSpace.Common
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
        private readonly ITerminationStrategy? _terminationStrategy;
        private TcpListener? _listener;
        private CancellationTokenSource? _serverCts;
        private readonly int _timeoutMs;

        // ---  EVENTS ---
        public event Action<TcpListenerState>? ListenerStateChanged;
        public event Action<string, bool, TcpClient?>? ClientConnectionChanged;
        public event Action<string, Exception>? ServerError;

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
            int maxBufferSize = 1000)
        {
            // Assign the default strategy if none was provided
            _listenPort = listenPort;
            _messageProcessor = messageProcessor;
            _logger = logger;
            _maxBufferSize = maxBufferSize;
            _terminationStrategy = termStrat;
            _timeoutMs = timeoutMs;

            _logger.Information("[Server:{Port}] Initialized with Strategy: {Strategy}",
                _listenPort, _terminationStrategy.GetType().Name);
        }

        private async Task StartSocketWatchdogAsync(CancellationToken token)
        {
            _logger.Information("[Server:{Port}] Heartbeat Watchdog started.", _listenPort);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    var now = DateTime.Now; // Swapped to Local Time

                    if (_timeoutMs > 0)
                    {
                        var timeout = TimeSpan.FromMilliseconds(_timeoutMs);
                        foreach (var kvp in _connectedClients)
                        {
                            if (now - kvp.Value.LastSeen > timeout)
                            {
                                _logger.Warning("[{ClientKey}] Watchdog: No heartbeat detected. Terminating.", kvp.Key);
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
        public List<string> GetConnectedClients() => _connectedClients.Keys.ToList();

        private async Task ListenForClientDataAsync(TcpClient client, string clientKey, CancellationToken token)
        {
            _logger.Debug("[{ClientKey}] Read loop started.", clientKey);

            // 1. Rent buffers from the shared pool
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(_maxBufferSize);
            byte[] messageBuffer = ArrayPool<byte>.Shared.Rent(_maxBufferSize);
            int messageLength = 0;

            try
            {
                await using NetworkStream stream = client.GetStream();

                while (client.Connected && !token.IsCancellationRequested)
                {
                    // Read into the rented buffer
                    var bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);

                    if (bytesRead == 0)
                    {
                        _logger.Warning("[{ClientKey}] Remote host closed connection.", clientKey);
                        DisconnectClient(clientKey);
                        break;
                    }

                    if (_connectedClients.TryGetValue(clientKey, out var conn))
                    {
                        _connectedClients[clientKey] = conn with { LastSeen = DateTime.UtcNow }; // Switched to UtcNow
                    }

                    // Process byte-by-byte
                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = readBuffer[i];
                        messageBuffer[messageLength++] = b;

                        // 2. Create a lightweight span of the current message state
                        var currentSpan = new ReadOnlySpan<byte>(messageBuffer, 0, messageLength);

                        if (_terminationStrategy.IsMessageComplete(currentSpan, b))
                        {
                            _logger.Debug("[{ClientKey}] ETX detected. Processing {Size} bytes.", clientKey,
                                messageLength);

                            // 3. Pass the rented array directly. No more .ToArray()!
                            var success = await _messageProcessor.ProcessMessageAsync(
                                stream,
                                messageBuffer,
                                messageLength,
                                clientKey,
                                token);

                            if (success)
                            {
                                messageLength = 0; // Reset index for the next message
                            }
                            else
                            {
                                _logger.Warning("[{ClientKey}] Processor returned failure. Disconnecting.", clientKey);
                            }
                        }

                        if (messageLength >= _maxBufferSize)
                        {
                            string utf8String = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
                            _logger.Error("[{ClientKey}] Buffer overflow. Clearing. {str}", clientKey, utf8String);
                            messageLength = 0;
                            DisconnectClient(clientKey);
                            break;
                        }
                    }
                }
            }
            catch (System.IO.IOException IOex) when (IOex.InnerException is SocketException se &&
                                                     se.SocketErrorCode == SocketError.ConnectionReset)
            {
                _logger.Warning("[{ClientKey}] Connection reset by peer.", clientKey);
                DisconnectClient(clientKey);
            }
            catch (OperationCanceledException)
            {
                // This is normal. It means the Watchdog timed out or the server is shutting down.
                _logger.Debug("[{ClientKey}] Read loop canceled gracefully.", clientKey);
                
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{ClientKey}] Error in read loop.", clientKey);
                NotifyError($"Error on connection {clientKey}", ex);
                DisconnectClient(clientKey);
            }
            finally
            {
                // 4. Critically important: Return the arrays to the pool when the connection ends
                ArrayPool<byte>.Shared.Return(readBuffer);
                ArrayPool<byte>.Shared.Return(messageBuffer);
            }
        }

        public async Task<bool> SendResponseAsync(string deviceName, string clientKey, object payload,
            CancellationToken token = default)
        {
            if (payload is not string msg )
            {
                _logger.Error("Bad Payload"); return false;}
            if (!_connectedClients.TryGetValue(clientKey, out var connection))
            {
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

                ClientConnectionChanged?.Invoke(clientKey, true, client);
                var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                Task clientTask = Task.Run(
                    () => ListenForClientDataAsync(client, clientKey, clientCts.Token),
                    clientCts.Token);

                var connection = new ClientConnection(client, clientCts, clientTask, DateTime.Now);

                if (!_connectedClients.TryAdd(clientKey, connection))
                {
                    _logger.Warning("[Server:{Port}] ClientKey {ClientKey} already exists.", _listenPort, clientKey);
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

        private void NotifyError(string context, Exception ex)
        {
            _logger.Error(ex, "[Server:{Port}] {Context}: {Message}", _listenPort, context, ex.Message);
            ServerError?.Invoke(context, ex);
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