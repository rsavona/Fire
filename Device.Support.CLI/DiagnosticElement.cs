using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Blueprints;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Device.Support.CLI;

/// <summary>
/// A Telnet-compatible diagnostic server that provides a real-time ANSI dashboard.
/// Connect via PuTTY (Port 9999) to view system-wide device statuses.
/// </summary>
public class DiagnosticElement : ElementBase<DiagnosticElement.State, DiagnosticElement.Event, DeviceMetric>
{
    public enum State { Offline, Running, Stopping }
    public enum Event { Start, Stop, ClientConnected, ClientDisconnected }

    private TcpListener? _listener;
    private bool _isRunning;
    private readonly int _port;
    private readonly DateTime _startTime;

    // ANSI Escape Sequences for Terminal Control
    private const string CLEAR_SCREEN = "\x1b[2J";
    private const string CURSOR_HOME = "\x1b[H";
    private const string HIDE_CURSOR = "\x1b[?25l";
    private const string SHOW_CURSOR = "\x1b[?25h";
    private const string DISABLE_WRAP = "\x1b[?7l";
    private const string ENABLE_WRAP = "\x1b[?7h";
    
    // SCO Save/Restore (More standard across various telnet clients)
    private const string SAVE_CURSOR = "\x1b[s";
    private const string RESTORE_CURSOR = "\x1b[u";

    public event Action<string, Guid>? OnCommandReceived;
    private readonly ConcurrentDictionary<Guid, StreamWriter> _clients = new();
    private readonly ConcurrentDictionary<Guid, int> _clientPages = new();
    private readonly ConcurrentDictionary<string, string> _statusTable = new();
    private readonly ConcurrentDictionary<string, DeviceAnnouncement> _announcements = new();
    private readonly ConcurrentDictionary<string, int> _deviceRows = new();
    private int _nextAvailableRow = 4; // Start after header

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _activeTraces = new();
    private readonly ConcurrentDictionary<Guid, bool> _clientLocked = new();
    private readonly ConcurrentDictionary<Guid, string> _clientUnlockBuffers = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _clientLastActivity = new();

    public DiagnosticElement(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch ls, IMessageBus mb) 
        : base(mb, config, logger, ls, State.Offline, Event.Start)
    {
        _startTime = DateTime.Now;
        _port = config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 9999;
        
        ConfigureStateMachine();

        // Subscribe to status updates to keep our internal table fresh
        MessageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleStatusMessageAsync);
        MessageBus.SubscribeAsync(MessageBusTopic.Discovery.ToString(), HandleDiscoveryMessageAsync);
    }

    private async Task HandleDiscoveryMessageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload is DeviceAnnouncement announcement)
        {
            _announcements[announcement.DeviceName.ToUpper()] = announcement;
            Logger.Debug("[DiagServer] Received discovery info for {Device}", announcement.DeviceName);
        }
        await Task.CompletedTask;
    }

    private async Task HandleTraceMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        string topic = envelope.Destination.ToString();
        if (_activeTraces.TryGetValue(topic, out var clientIds))
        {
            string payload = envelope.Payload?.ToString() ?? "NULL";
            string msg = $"\x1b[90m[TRACE:{topic}]\x1b[0m {payload}";
            
            foreach (var id in clientIds.Keys)
            {
                // Send trace messages starting a bit lower to not interfere with prompt as much
                Reply(id, msg, _nextAvailableRow + 12);
            }
        }
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .Permit(Event.Start, State.Running);

        Machine.Configure(State.Running)
            .OnEntry(StartServer)
            .OnExit(StopServer)
            .Permit(Event.Stop, State.Stopping);

        Machine.Configure(State.Stopping)
            .OnEntry(() => Machine.Fire(Event.Stop))
            .Permit(Event.Stop, State.Offline);
    }

    private async Task UptimeLoggingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested || !_isRunning) break;

            var up = DateTime.Now - _startTime;
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var ram = process.WorkingSet64 / 1024 / 1024; // MB

            Logger.Information("[DiagServer] Periodic Uptime Report | Uptime: {Days}d {Hours}h {Minutes}m | RAM: {Ram}MB | Threads: {Threads}",
                up.Days, up.Hours, up.Minutes, ram, process.Threads.Count);
        }
    }

    private void StartServer()
    {
        try 
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _isRunning = true;
            RegisterTask(Task.Run(AcceptClientsAsync));
            RegisterTask(Task.Run(() => UptimeLoggingLoopAsync(ConnectionToken)));
            RegisterTask(Task.Run(() => InactivityMonitorLoopAsync(ConnectionToken)));
            Logger.Information("[DiagServer] Telnet Diagnostic Dashboard started on port {Port}", _port);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[DiagServer] Failed to start on port {Port}", _port);
        }
    }

    private void StopServer()
    {
        _isRunning = false;
        _listener?.Stop();
        CancelSession();
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch { }
        }
        _clients.Clear();
    }

    private async Task HandleStatusMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (message?.Payload is not DeviceStatusMessage msg) return;

        string name = msg.DeviceId.DeviceName.ToUpper();
        if (name.Contains("MANAGER", StringComparison.OrdinalIgnoreCase)) return;

        // 1. Assign a row if new (Thread-safe assignment)
        int row = _deviceRows.GetOrAdd(name, _ => Interlocked.Increment(ref _nextAvailableRow));

        // 2. Format the line (ANSI)
        string formattedLine = FormatStatusLine(msg);
        _statusTable[name] = formattedLine;

        // 3. Broadcast to all telnet clients with cursor positioning
        BroadcastToRow(row, formattedLine);
    }

    private string FormatStatusLine(DeviceStatusMessage msg)
    {
        var name = msg.DeviceId.DeviceName.ToUpper();
        if (name.Length > 14) name = name.Substring(0, 14);

        var stateStr = msg.State;
        if (stateStr.Length > 12) stateStr = stateStr.Substring(0, 12);

        var color = DeviceHealthExtension.ToAnsiColor(msg.Health);
        var reset = "\x1b[0m";
        var div = "\x1b[90m|\x1b[0m";

        // Heartbeat icon (toggling colors)
        string hbString = msg.HbVisual == 'H' ? "\x1b[34m* \x1b[0m" : (msg.HbVisual == ' ' ? "\x1b[30m* \x1b[0m" : "\x1b[31m* \x1b[0m");
        
        // Connections / Disconnects
        string cdString = $"{msg.CountConnections % 100,2}\x1b[90m/\x1b[0m{msg.CountDisconnects % 100,-2}";
        
        // Avg Process Time (with color thresholding)
        var apt = Math.Round(msg.AvgProcessTime, 1);
        var aptColor = apt < 30 ? "\x1b[92m" : (apt < 100 ? "\x1b[93m" : "\x1b[91m");
        string aptString = $"{aptColor}> {apt:000.0}\x1b[0m";
        
        // I/O Stats
        string ioString = $"\x1b[96mv\x1b[0m{msg.CountInbound,4}\x1b[90m|\x1b[0m\x1b[36m^\x1b[0m{msg.CountOutbound,4}";
        
        // Error Indicator
        string errString = msg.CountError > 0 ? $"\x1b[91mX {msg.CountError,-2}\x1b[0m" : $"\x1b[92mO 0 \x1b[0m";

        // Resource Stats: Tasks / Containers / DeepCount
        string resString = $"\x1b[94m{msg.ResourceTasks,2}\x1b[90m/\x1b[94m{msg.ResourceContainers,1}\x1b[90m/\x1b[94m{msg.ResourceDeepCount,-3}\x1b[0m";
        
        // Local timestamp
        var displayTime = msg.Timestamp.ToLocalTime().ToString("hh:mm:ss");

        // Truncate comment to fit screen
        string comment = msg.Comment ?? "";
        if (comment.Length > 60) comment = comment.Substring(0, 57) + "...";

        return $"{name,-14}{hbString}{color}{stateStr,-12}{reset}{div} {cdString} {div}{aptString}{div}{ioString}{div} {errString} {div} {resString} {div}T {displayTime}{div}{comment,-60}";
    }

    private void BroadcastToRow(int row, string text)
    {
        // Use SCO Save/Restore for better terminal compatibility
        // Move to row, write text, then clear to end of line to avoid blanking start of line
        string packet = $"{SAVE_CURSOR}\x1b[{row};1H{text}\x1b[K{RESTORE_CURSOR}";
        foreach (var client in _clients.Values)
        {
            try { client.Write(packet); } catch { /* Socket likely closed */ }
        }
    }

    private async Task AcceptClientsAsync()
    {
        while (_isRunning && _listener != null)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                RegisterTask(HandleNewConnection(tcpClient));
            }
            catch { break; }
        }
    }

    private async Task InactivityMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var now = DateTime.Now;
            foreach (var id in _clientLastActivity.Keys)
            {
                if (_clientLastActivity.TryGetValue(id, out var last) && (now - last) > TimeSpan.FromSeconds(30))
                {
                    if (!_clientLocked.TryGetValue(id, out var isLocked) || !isLocked)
                    {
                        _clientLocked[id] = true;
                        _clientUnlockBuffers[id] = "";
                        UpdateClientHeader(id);
                    }
                }
            }
        }
    }

    private void UpdateClientHeader(Guid id)
    {
        if (_clients.TryGetValue(id, out var writer))
        {
            bool isLocked = _clientLocked.GetOrAdd(id, true);
            string lockStatus = isLocked ? "\x1b[91mLOCKED" : "\x1b[92mUNLOCKED";
            writer.Write($"{SAVE_CURSOR}\x1b[1;140H {lockStatus}\x1b[0m {RESTORE_CURSOR}");
        }
    }

    private async Task HandleNewConnection(TcpClient client)
    {
        Guid clientId = Guid.NewGuid();
        try
        {
            var stream = client.GetStream();
            var utf8NoBom = new UTF8Encoding(false);
            var writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };
            var reader = new StreamReader(stream, utf8NoBom);

            if (_clients.TryAdd(clientId, writer))
            {
                _clientLocked[clientId] = true;
                _clientLastActivity[clientId] = DateTime.Now;
                _clientUnlockBuffers[clientId] = "";

                // Initialize the terminal: Clear, Home, Hide Cursor, Disable Wrap
                writer.Write(CLEAR_SCREEN + CURSOR_HOME + HIDE_CURSOR + DISABLE_WRAP);
                
                string header = $"\x1b[1;1H\x1b[48;2;0;90;190m\x1b[37m FORTNA FIRE REMOTE DASHBOARD - HOST: {Config.Name} | STARTED: {_startTime:HH:mm:ss} \x1b[0m";
                string subHeader = $"\x1b[2;1H\x1b[90m{"Device CustomerName",-14} HB {"Status",-12} | Connect | PsTm ms | ↓IN /^OUT | Errors | Res:T/C/D | Time     | Comment\x1b[0m";
                string divLine = $"\x1b[3;1H\x1b[90m{new string('-', 160)}\x1b[0m";

                writer.Write(header + subHeader + divLine);
                UpdateClientHeader(clientId);
                
                // Send current snapshot immediately so they don't wait for updates
                foreach (var entry in _deviceRows)
                {
                    if (_statusTable.TryGetValue(entry.Key, out var line))
                    {
                        writer.Write($"\x1b[{entry.Value};1H{line}");
                    }
                }

                await ClientReadLoop(clientId, reader, client);
            }
        }
        catch { client.Close(); }
    }

    private async Task ClientReadLoop(Guid id, StreamReader reader, TcpClient client)
    {
        try
        {
            _clientPages[id] = 1;
            while (_isRunning)
            {
                // Position prompt below the last device. Use SAVE_CURSOR so background updates restore correctly.
                int promptRow = Math.Max(_nextAvailableRow + 1, 6);
                Reply(id, "Command (HELP for list) > ", promptRow, true);

                var cmd = await reader.ReadLineAsync();
                if (cmd == null) break;
                
                _clientLastActivity[id] = DateTime.Now;

                string input = cmd.Trim();
                if (input.ToUpper() == "EXIT" || input.ToUpper() == "QUIT") break;
                
                if (_clientLocked.TryGetValue(id, out var isLocked) && isLocked)
                {
                    var buffer = _clientUnlockBuffers.GetOrAdd(id, "") + input.ToLower();
                    if (buffer.Contains("fortna"))
                    {
                        _clientLocked[id] = false;
                        _clientUnlockBuffers[id] = "";
                        UpdateClientHeader(id);
                        Reply(id, "Console UNLOCKED.", promptRow + 1);
                    }
                    else
                    {
                        // Maintain a small buffer
                        if (buffer.Length > 20) buffer = buffer.Substring(buffer.Length - 10);
                        _clientUnlockBuffers[id] = buffer;
                        Reply(id, "Console LOCKED. Type 'fortna' to unlock.", promptRow + 1);
                    }
                    continue;
                }

                if (!HandleInternalCommand(id, input))
                {
                    OnCommandReceived?.Invoke(input, id);
                }
            }
        }
        finally
        {
            _clientPages.TryRemove(id, out _);
            _clientLocked.TryRemove(id, out _);
            _clientUnlockBuffers.TryRemove(id, out _);
            _clientLastActivity.TryRemove(id, out _);
            if (_clients.TryRemove(id, out var writer))
            {
                try { writer.Write(SHOW_CURSOR + ENABLE_WRAP); } catch { }
            }
            client.Close();
        }
    }

    private bool HandleInternalCommand(Guid clientId, string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        string verb = parts[0].ToUpper();
        int replyRow = _nextAvailableRow + 2;

        switch (verb)
        {
            case "1":
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Information;
                Reply(clientId, "Global Log Level set to INFORMATION", replyRow);
                return true;

            case "2":
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Debug;
                Reply(clientId, "Global Log Level set to DEBUG", replyRow);
                return true;

            case "3":
                LogControl.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
                Reply(clientId, "Global Log Level set to VERBOSE", replyRow);
                return true;

            case "T":
                var topicT = MessageBusTopic.ConsoleCommand.ToString();
                _ = MessageBus.PublishAsync(topicT, new MessageEnvelope(MessageBusTopic.ConsoleCommand, "RELEASE_TOTE"));
                Reply(clientId, "Sent RELEASE_TOTE command to bus", replyRow);
                return true;

            case "L":
                RefreshClient(clientId);
                var topicRefresh = MessageBusTopic.SystemControl.ToString();
                var msgRefresh = new SystemControlMessage(SystemCommand.RefreshStatus);
                _ = MessageBus.PublishAsync(topicRefresh, new MessageEnvelope(MessageBusTopic.SystemControl, msgRefresh));
                Reply(clientId, "Triggered Global Status Refresh", replyRow);
                return true;

            case "HELP":
                ShowHelp(clientId, replyRow);
                return true;

            case "NEXT":
                int currentPage = _clientPages.GetOrAdd(clientId, 1);
                _clientPages[clientId] = currentPage == 1 ? 2 : 1;
                ShowHelp(clientId, replyRow);
                return true;

            case "LOG":
                if (parts.Length >= 3)
                {
                    string devName = parts[1];
                    if (Enum.TryParse<LogEventLevel>(parts[2], true, out var level))
                    {
                        LogControl.SetDeviceLevel(devName, level);
                        Reply(clientId, $"Log level for {devName} set to {level}", replyRow);
                    }
                    else
                    {
                        Reply(clientId, $"Invalid log level: {parts[2]}. Use Verbose, Debug, Info, Warning, Error", replyRow);
                    }
                }
                else
                {
                    Reply(clientId, "Usage: LOG [DeviceName] [Level]", replyRow);
                }
                return true;

            case "CLS":
                RefreshClient(clientId);
                return true;

            case "UPTIME":
                ShowUptime(clientId);
                return true;

            case "DESC":
                if (parts.Length >= 2)
                {
                    string targetDev = parts[1].ToUpper();
                    ShowDeviceDescription(clientId, targetDev, replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: DESC [DeviceName]", replyRow);
                }
                return true;

            case "RESTART":
                if (parts.Length >= 2)
                {
                    string targetDev = parts[1].ToUpper();
                    _ = PublishControlCommand(targetDev, "RESTART");
                    Reply(clientId, $"Sent RESTART command for {targetDev}", replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: RESTART [DeviceName]", replyRow);
                }
                return true;

            case "ONLINE":
                if (parts.Length >= 2)
                {
                    string targetDev = parts[1].ToUpper();
                    _ = PublishControlCommand(targetDev, "ONLINE");
                    Reply(clientId, $"Sent ONLINE command for {targetDev}", replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: ONLINE [DeviceName]", replyRow);
                }
                return true;

            case "OFFLINE":
                if (parts.Length >= 2)
                {
                    string targetDev = parts[1].ToUpper();
                    _ = PublishControlCommand(targetDev, "OFFLINE");
                    Reply(clientId, $"Sent OFFLINE command for {targetDev}", replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: OFFLINE [DeviceName]", replyRow);
                }
                return true;

            case "TRACE":
                if (parts.Length >= 2)
                {
                    string topic = parts[1].ToUpper();
                    var clients = _activeTraces.GetOrAdd(topic, _ => 
                    {
                        MessageBus.SubscribeAsync(topic, (Func<MessageEnvelope, CancellationToken, Task>)HandleTraceMessageAsync);
                        return new ConcurrentDictionary<Guid, byte>();
                    });
                    clients.TryAdd(clientId, 0);
                    Reply(clientId, $"Tracing topic: {topic}", replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: TRACE [Topic]", replyRow);
                }
                return true;

            case "UNTRACE":
                if (parts.Length >= 2)
                {
                    string topic = parts[1].ToUpper();
                    if (_activeTraces.TryGetValue(topic, out var clients))
                    {
                        clients.TryRemove(clientId, out _);
                        Reply(clientId, $"Stopped tracing topic: {topic}", replyRow);
                    }
                }
                else
                {
                    foreach (var kvp in _activeTraces)
                    {
                        kvp.Value.TryRemove(clientId, out _);
                    }
                    Reply(clientId, "Stopped all tracing", replyRow);
                }
                return true;

            case "PUB":
                if (parts.Length >= 3)
                {
                    string topicStr = parts[1].ToUpper();
                    string payload = string.Join(" ", parts.Skip(2));
                    var topic = new MessageBusTopic(topicStr);
                    _ = MessageBus.PublishAsync(topicStr, new MessageEnvelope(topic, payload));
                    Reply(clientId, $"Published message to {topicStr}", replyRow);
                }
                else
                {
                    Reply(clientId, "Usage: PUB [Topic] [Payload]", replyRow);
                }
                return true;
        }

        return false;
    }

    private void ShowUptime(Guid clientId)
    {
        var up = DateTime.Now - _startTime;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var ram = process.WorkingSet64 / 1024 / 1024; // MB
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("--- SYSTEM STATUS ---");
        sb.AppendLine($"Uptime:    {up.Days}d {up.Hours}h {up.Minutes}m {up.Seconds}s");
        sb.AppendLine($"RAM Usage: {ram} MB");
        sb.AppendLine($"Threads:   {process.Threads.Count}");
        sb.AppendLine($"Start:     {_startTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("---------------------");
        Reply(clientId, sb.ToString(), _nextAvailableRow + 2);
    }

    private async Task PublishControlCommand(string deviceName, string command)
    {
        // Topic: SYS.CONTROL.DEVICE_NAME
        var topic = new MessageBusTopic("SYS", "CONTROL", deviceName);
        var payload = new { Command = command, Timestamp = DateTime.UtcNow };
        await MessageBus.PublishAsync(topic.ToString(), new MessageEnvelope(topic, payload));
    }

    private void ShowDeviceDescription(Guid clientId, string deviceName, int startRow)
    {
        if (_announcements.TryGetValue(deviceName, out var ann))
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"--- DEVICE DESCRIPTION: {ann.DeviceName} ---");
            sb.AppendLine($"Type:     {ann.DeviceType}");
            sb.AppendLine($"Version:  {ann.SoftwareVersion}");
            sb.AppendLine($"Runtime:  {ann.Runtime}");
            sb.AppendLine($"Commands: {string.Join(", ", ann.AvailableCommands.Select(c => c.CommandName))}");
            sb.AppendLine("------------------------------------------");
            Reply(clientId, sb.ToString(), startRow);
        }
        else
        {
            Reply(clientId, $"No description available for {deviceName}. Wait for discovery announcement.", startRow);
        }
    }

    private void ShowHelp(Guid clientId, int startRow)
    {
        int page = _clientPages.GetOrAdd(clientId, 1);
        StringBuilder sb = new StringBuilder();
        
        if (page == 1)
        {
            sb.AppendLine("--- AVAILABLE COMMANDS (PAGE 1/2) ---");
            sb.AppendLine("LOG [Device] [Level] - Set log level (e.g. LOG TPNA2 Debug)");
            sb.AppendLine("DESC [Device]        - Show device metadata & supported commands");
            sb.AppendLine("RESTART [Device]     - Cycle a device offline then online");
            sb.AppendLine("ONLINE [Device]      - Set device state to ONLINE");
            sb.AppendLine("OFFLINE [Device]     - Set device state to OFFLINE");
            sb.AppendLine("NEXT                 - Show next page of commands");
            sb.AppendLine("HELP                 - Show this help");
            sb.AppendLine("-------------------------------------");
        }
        else
        {
            sb.AppendLine("--- AVAILABLE COMMANDS (PAGE 2/2) ---");
            sb.AppendLine("TRACE [Topic]        - Stream messages from a topic");
            sb.AppendLine("UNTRACE [Topic]      - Stop streaming a topic");
            sb.AppendLine("PUB [Topic] [Msg]    - Manually inject a message into the bus");
            sb.AppendLine("UPTIME               - Show system resource stats");
            sb.AppendLine("CLS                  - Clear and refresh the dashboard");
            sb.AppendLine("NEXT                 - Show previous page of commands");
            sb.AppendLine("EXIT/QUIT            - Disconnect");
            sb.AppendLine("-------------------------------------");
        }

        Reply(clientId, sb.ToString(), startRow);
    }

    private void RefreshClient(Guid clientId)
    {
        if (_clients.TryGetValue(clientId, out var writer))
        {
            writer.Write(CLEAR_SCREEN + CURSOR_HOME + HIDE_CURSOR + DISABLE_WRAP);
            
            string header = $"\x1b[1;1H\x1b[48;2;0;90;190m\x1b[37m FORTNA FIRE REMOTE DASHBOARD - HOST: {Config.Name} | STARTED: {_startTime:HH:mm:ss} \x1b[0m";
            string subHeader = $"\x1b[2;1H\x1b[90m{"Device CustomerName",-14} HB {"Status",-12} | Connect | PsTm ms | ↓IN /^OUT | Errors | Res:T/C/D | Time     | Comment\x1b[0m";
            string divLine = $"\x1b[3;1H\x1b[90m{new string('-', 160)}\x1b[0m";

            writer.Write(header + subHeader + divLine);

            foreach (var entry in _deviceRows)
            {
                if (_statusTable.TryGetValue(entry.Key, out var line))
                {
                    writer.Write($"\x1b[{entry.Value};1H{line}");
                }
            }
        }
    }

    public void Reply(Guid clientId, string message, int startRow = 45, bool saveCursor = false)
    {
        if (_clients.TryGetValue(clientId, out var writer))
        {
            try 
            { 
                var lines = message.Split('\n');
                foreach(var line in lines)
                {
                    writer.Write($"\x1b[{startRow++};1H{line.TrimEnd('\r')}\x1b[K");
                }
                // Also clear a few lines below to keep the prompt area tidy
                writer.Write($"\x1b[{startRow};1H\x1b[K");

                // If this is a persistent prompt, save the cursor position here
                if (saveCursor) writer.Write(SAVE_CURSOR);
            } 
            catch { }
        }
    }

    public override async Task StartAsync(CancellationToken token)
    {
        PrepareSessionToken(token);
        await Machine.FireAsync(Event.Start);
    }
    public override async Task StopAsync(CancellationToken token)
    {
        Logger.Information("[{Dev}] Shutting down gracefully...", Config.Name);
        await Machine.FireAsync(Event.Stop);
    }

    protected override DeviceHealth MapStateToHealth(State state) => state switch
    {
        State.Running => DeviceHealth.Normal,
        State.Stopping => DeviceHealth.Warning,
        State.Offline => DeviceHealth.Warning,
        _ => DeviceHealth.Warning
    };
}
