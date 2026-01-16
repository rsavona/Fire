using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Serilog;

namespace Device.Support.CLI;

public class DiagnosticDevice 
{
    private TcpListener _listener;
    private bool _isRunning;
    private readonly int _port;
    private readonly IMessageBus _messageBus;
    
    // Event: Your main program subscribes to this to react to commands
    public event Action<string, Guid> OnCommandReceived;

    // Store writers so we can talk back
    private ConcurrentDictionary<Guid, StreamWriter> _clients = new ConcurrentDictionary<Guid, StreamWriter>();

    private string? _name;
    public DiagnosticDevice(IMessageBus mb, IDeviceConfig conf, ILogger logger) 
    {
        var config = ConfigurationLoader.GetSpaceConfig();
        _port = 9999;
        _messageBus = mb;
        _name = config?.Name;
    }

    public void Start()
    {
        try 
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _isRunning = true;
            Task.Run(AcceptClientsAsync);
            var stat = new DeviceStatusMessage(new DeviceKey("SYS","DiagServer"), "Online", DeviceHealth.Normal, 
                        $"Started on port {_port}.",0,0,0,0);
            
            _messageBus.PublishStatusAsync("DiagnosticServer", stat);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiagServer] Failed to start: {ex.Message}");
        }
    }

    private async Task AcceptClientsAsync()
    {
        while (_isRunning)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                HandleNewConnection(tcpClient);
            }
            catch { /* Listener stopped */ }
        }
    }

    private void HandleNewConnection(TcpClient client)
    {
        Guid clientId = Guid.NewGuid();
        try
        {
            var stream = client.GetStream();
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
            var reader = new StreamReader(stream, Encoding.ASCII);

            if (_clients.TryAdd(clientId, writer))
            {
                writer.WriteLine($"--- CONNECTED ({_name}) ---");
                writer.WriteLine("Type 'HELP' for commands.");
                
                // Start a background task specifically to listen to THIS client
                Task.Run(() => ClientReadLoop(clientId, reader, client));
            }
        }
        catch { client.Close(); }
    }

    // THE NEW PART: Listening for input from PuTTY
    private async Task ClientReadLoop(Guid id, StreamReader reader, TcpClient client)
    {
        try
        {
            while (_isRunning)
            {
                // Wait for the user to press Enter
                var cmd = await reader.ReadLineAsync();
                
                if (cmd == null) break; // Connection closed
                if (string.IsNullOrWhiteSpace(cmd)) continue;

                // Send to main program
                OnCommandReceived?.Invoke(cmd.Trim(), id);
                var topic = new MessageBusTopic("DiagnosticServer", "Cmd", "Graphable"); 
                await _messageBus.PublishAsync(topic.ToString(), new MessageEnvelope( topic,".\\Graphs\\"));
            }
        }
        catch (Exception e)
        {
            var stat = new DeviceStatusMessage(new DeviceKey("SYS","DiagServer"), "Online", DeviceHealth.Normal, 
                $"Internal error - reconnect to {_port}.",0,0,0,0);
            
            await _messageBus.PublishStatusAsync("DiagnosticServer", stat);
        }
        finally
        {
            RemoveClient(id);
            client.Close();
        }
    }

    public void Log(string message)
    {
        if (_clients.IsEmpty) return;
        string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        
        foreach (var kvp in _clients)
        {
            try { kvp.Value.WriteLine(logEntry); }
            catch { /* Lazy cleanup handles this later */ }
        }
    }

    // Send a message to ONE specific client (e.g. reply to a command)
    public void Reply(Guid clientId, string message)
    {
        if (_clients.TryGetValue(clientId, out var writer))
        {
            try { writer.WriteLine(">> " + message); } catch { }
        }
    }

    private void RemoveClient(Guid id)
    {
        _clients.TryRemove(id, out _);
    }

    protected  Task HandleReceivedDataAsync(string clientId, byte[] buffer, int bytesRead, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public void ExportAllDeviceMaps(string mapExportPath)
    {
        throw new NotImplementedException();
    }
}