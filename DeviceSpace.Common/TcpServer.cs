using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.Contracts;
using Serilog;

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
        private const char ETX = (char)0x03;
        private const int RETRY_DELAY_SECONDS = 30;

        private readonly int _maxBufferSize;
        private readonly int _listenPort;
        private readonly IMessageProcessor _messageProcessor;
        private readonly ConcurrentDictionary<string, ClientConnection> _connectedClients = new();
        private readonly ILogger _logger;

        private TcpListener? _listener;
        private CancellationTokenSource? _serverCts;

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
            ILogger logger,
            int maxBufferSize = 1000)
        {
            _listenPort = listenPort;
            _messageProcessor = messageProcessor;
            _logger = logger;
            _maxBufferSize = maxBufferSize;
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
                    var timeout = TimeSpan.FromSeconds(60);

                    foreach (var kvp in _connectedClients)
                    {
                        if (now - kvp.Value.LastSeen > timeout)
                        {
                            _logger.Warning("[{ClientKey}] Watchdog: No heartbeat detected. Terminating.", kvp.Key);
                            _ = CleanupClient(kvp.Key);
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

        private async Task ListenForClientDataAsync(TcpClient client, string clientKey, CancellationToken token)
        {
            _logger.Debug("[{ClientKey}] Read loop started.", clientKey);
            try
            {
                await using NetworkStream stream = client.GetStream();
                var buffer = new byte[_maxBufferSize];
                var receiveBuffer = new List<byte>();

                while (client.Connected && !token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, token);

                    if (bytesRead == 0)
                    {
                        _logger.Warning("[{ClientKey}] Remote host closed connection.", clientKey);
                        DisconnectClient(clientKey);
                        break;
                    }

                    // Update LastSeen using Local Time
                    if (_connectedClients.TryGetValue(clientKey, out var conn))
                    {
                        _connectedClients[clientKey] = conn with { LastSeen = DateTime.Now };
                    }

                    // Process byte-by-byte to handle fragmented or merged packets
                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = buffer[i];
                        receiveBuffer.Add(b);

                        if (b == (byte)ETX)
                        {
                            string payload = Encoding.ASCII.GetString(receiveBuffer.ToArray());

                            _logger.Debug("[{ClientKey}] ETX detected. Processing {Size} bytes.", clientKey,
                                receiveBuffer.Count);


                            var success = await _messageProcessor.ProcessMessageAsync(stream, receiveBuffer.ToArray(),
                                receiveBuffer.Count, clientKey, token);

                            if (success)
                            {
                                receiveBuffer.Clear();
                            }
                            else
                            {
                                _logger.Warning("[{ClientKey}] Processor returned failure. Disconnecting.", clientKey);
                            }
                        }

                        if (receiveBuffer.Count > _maxBufferSize)
                        {
                            _logger.Error("[{ClientKey}] Buffer overflow. Clearing.", clientKey);
                            receiveBuffer.Clear();
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
            catch (Exception ex)
            {
                _logger.Error(ex, "[{ClientKey}] Error in read loop.", clientKey);
                NotifyError($"Error on connection {clientKey}", ex);
                DisconnectClient(clientKey);
            }
        }

        public async Task<bool> SendResponseAsync(string deviceName, string clientKey, string? payload,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(payload)) return false;

            var message = _messageProcessor.HandleResponse(deviceName, payload);

            if (!_connectedClients.TryGetValue(clientKey, out var connection))
            {
                _logger.Warning("[{ClientKey}] Send failed: Client not found.", clientKey);
                return false;
            }

            try
            {
                byte[] bytesToSend = Encoding.ASCII.GetBytes(message);
                await connection.Client.GetStream().WriteAsync(bytesToSend, token);

                // Logging and Event in Local Time
                _logger.Information("[{ClientKey}] Sent: {msg}", clientKey, message);
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
            Task watchdogTask = StartSocketWatchdogAsync(_serverCts.Token);
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
                await watchdogTask;
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