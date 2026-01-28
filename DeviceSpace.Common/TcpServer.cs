using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.Contracts;
using Serilog;

namespace DeviceSpace.Common
{
    public enum TcpListenerState { Stopped, Starting, Listening, FailedAddressInUse, FailedRetrying, Cancelled, Exception }

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

        private record ClientConnection(TcpClient Client, CancellationTokenSource Cts, Task ClientTask);

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

        private void SetListenerState(TcpListenerState newState)
        {
            _logger.Information("[Server:{Port}] Listener state changed to {State}", _listenPort, newState);
            try
            {
                ListenerStateChanged?.Invoke(newState);
            }
            catch (Exception ex)
            {
                NotifyError("SetListenerState", ex);
            }
        }

        private void NotifyError(string context, Exception ex)
        {
            _logger.Error(ex, "[Server:{Port}] Error in {Context}: {Message}", _listenPort, context, ex.Message);
            try
            {
                ServerError?.Invoke(context, ex);
            }
            catch { /* Prevent recursive crashes */ }
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
                _logger.Warning("[Server:{Port}] Server execution loop terminated.", _listenPort);
            }
        }

        private async Task ListenForClientConnectionsAsync(CancellationToken token)
        {
            bool listenerStarted = false;
            while (!listenerStarted && !token.IsCancellationRequested)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, _listenPort);
                    _listener.Server.ExclusiveAddressUse = true;
                    _listener.Start();
                    listenerStarted = true;
                    SetListenerState(TcpListenerState.Listening);

                    while (!token.IsCancellationRequested)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync(token);
                        if (client.Client.RemoteEndPoint is IPEndPoint remoteIpEndPoint)
                        {
                            string clientKey = $"{remoteIpEndPoint.Address}:{remoteIpEndPoint.Port}";
                            
                            // UPDATED: Pass the client instance to the event
                            ClientConnectionChanged?.Invoke(clientKey, true, client);
                            
                            _logger.Information("[Server:{Port}] New connection accepted from {ClientKey}", _listenPort, clientKey);

                            var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            Task clientTask = Task.Factory.StartNew(
                                () => ListenForClientDataAsync(client, clientKey, clientCts.Token),
                                clientCts.Token,
                                TaskCreationOptions.LongRunning,
                                TaskScheduler.Default);

                            var connection = new ClientConnection(client, clientCts, clientTask);
                            if (!_connectedClients.TryAdd(clientKey, connection))
                            {
                                _logger.Information("[Server:{Port}] Could not add client {ClientKey}", _listenPort, clientKey);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("[Server:{Port}] Listener operation canceled.", _listenPort);
                    SetListenerState(TcpListenerState.Cancelled);
                }
                catch (Exception ex)
                {
                    NotifyError("Fatal Listener Error. Retrying...", ex);
                    SetListenerState(TcpListenerState.FailedRetrying);
                    _listener?.Stop();
                    await Task.Delay(TimeSpan.FromSeconds(RETRY_DELAY_SECONDS), token);
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
                        _logger.Warning("[{ClientKey}] Remote host closed the connection (Received 0 bytes).", clientKey);
                        break;
                    }

                    _logger.Verbose("[{ClientKey}] Read {Bytes} bytes from stream.", clientKey, bytesRead);
                    receiveBuffer.AddRange(new ArraySegment<byte>(buffer, 0, bytesRead));

                    if (receiveBuffer.Count > _maxBufferSize)
                    {
                        _logger.Error("[{ClientKey}] Buffer overflow! Current: {Size}, Max: {Max}", clientKey, receiveBuffer.Count, _maxBufferSize);
                        throw new IOException($"Client {clientKey} exceeded max buffer size.");
                    }

                    if (buffer[bytesRead - 1] == ETX)
                    {
                        _logger.Debug("[{ClientKey}] ETX detected. Processing message of {Size} bytes.", clientKey, receiveBuffer.Count);
                        
                        var success = await _messageProcessor.ProcessMessageAsync(stream, receiveBuffer.ToArray(), receiveBuffer.Count, clientKey, token);
                        
                        if (success)
                        {
                             _logger.Debug("[{ClientKey}] Message processed successfully. Clearing buffer.", clientKey);
                             receiveBuffer.Clear(); 
                        }
                        else 
                        {
                            _logger.Warning("[{ClientKey}] Message processor returned 'false'. Disconnecting client.", clientKey);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{ClientKey}] Error in read loop.", clientKey);
                NotifyError($"Error on connection {clientKey}", ex);
            }
            finally
            {
                await CleanupClient(clientKey);
            }
        }

        public async Task<bool> SendResponseAsync(string deviceName, string clientKey, string? payload, CancellationToken token = default)
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
                return true;
            }
            catch (Exception ex)
            {
                NotifyError($"Failed to send data to {clientKey}", ex);
                await CleanupClient(clientKey);
                return false;
            }
        }

        private Task CleanupClient(string key)
        {
            return Task.Run(() =>
            {
                if (_connectedClients.TryRemove(key, out var connection))
                {
                    _logger.Information("[{ClientKey}] Cleaning up client resources.", key);
                    try
                    {
                        // UPDATED: Pass null for client on disconnect
                        ClientConnectionChanged?.Invoke(key, false, null);

                        if (!connection.Cts.IsCancellationRequested)
                            connection.Cts.Cancel();

                        if (connection.Client.Connected)
                        {
                            connection.Client.Client.Shutdown(SocketShutdown.Both);
                        }

                        connection.Client.Close();
                        connection.Client.Dispose();
                        connection.Cts.Dispose();
                    }
                    catch (Exception ex)
                    {
                        NotifyError("CleanupClient Error", ex);
                    }
                }
            });
        }

        public void Dispose()
        {
            _logger.Information("[Server:{Port}] Disposing Server...", _listenPort);
            _serverCts?.Cancel();
            _listener?.Stop();
            foreach (var key in _connectedClients.Keys) _ = CleanupClient(key);
        }

        public async Task StopAsync()
        {
            _logger.Information("[Server:{Port}] Stopping Server...", _listenPort);
            try
            {
                _listener?.Stop();
                foreach (var clientKey in _connectedClients.Keys)
                {
                   await CleanupClient(clientKey);
                }
            }
            catch (Exception ex)
            {
                ServerError?.Invoke("StopAsync", ex);
            }
        }

        public void DisconnectClient(string key)
        {
            _logger.Information("[{ClientKey}] Manual disconnect requested.", key);
            if (_connectedClients.ContainsKey(key))
            {
                _ = CleanupClient(key);
            }
        }
    }
}