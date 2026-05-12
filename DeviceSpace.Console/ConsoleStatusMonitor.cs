using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Microsoft.Extensions.Logging;

namespace DeviceSpaceConsole;

public class ConsoleStatusMonitor
{
    private static readonly Lock ConsoleLock = new();

    private const int START_ERROR = 30;

    // Thread-safe dictionary to store the row index for each unique device/workflow
    private readonly ConcurrentDictionary<string, int> _deviceRowMap = new();
    private readonly ConcurrentDictionary<string, int> _devicePriorityMap = new();
    private int _lines;
    private int _errorLine;

    private int GetDevicePriority(DeviceStatusMessage msg)
    {
        var scope = msg.DeviceId.ScopeName.ToUpperInvariant();
        var name = msg.DeviceId.DeviceName.ToUpperInvariant();

        if (scope == "SYSTEM") return 10; // Workflows first
        if (name.Contains("PLC")) return 20;
        if (name.Contains("PRINTER") || name.Contains("JETMARK") || name.Contains("ZEBRA")) return 30;
        if (name.Contains("HOST") || name.Contains("TCP") || name.Contains("CLIENT")) return 40;
        if (name.Contains("MQ") || name.Contains("NATS") || name.Contains("BUS")) return 50;
        
        return 100; // Others last
    }

    private int GetRowIndex(DeviceStatusMessage msg)
    {
        var name = msg.DeviceId.DeviceName.ToUpper().Trim();
        if (_deviceRowMap.TryGetValue(name, out int existingIndex)) return existingIndex;

        // New device! Re-calculate all row indexes based on priority and name
        lock (_deviceRowMap)
        {
            if (_deviceRowMap.TryGetValue(name, out existingIndex)) return existingIndex;

            _devicePriorityMap[name] = GetDevicePriority(msg);
            
            var sortedNames = _devicePriorityMap
                .OrderBy(kvp => kvp.Value)           // Primary: Priority
                .ThenBy(kvp => kvp.Key)              // Secondary: Alphabetical
                .Select(kvp => kvp.Key)
                .ToList();

            for (int i = 0; i < sortedNames.Count; i++)
            {
                _deviceRowMap[sortedNames[i]] = i;
            }

            return _deviceRowMap[name];
        }
    }
    private int _startingLineOffset = 0;
    private DateTime _startTime;
    private readonly IFireLogger _logger;
    private bool _isPaused = false;
    private bool _isActive = true;
    private bool _isLocked = true;
    private readonly bool _useColor;

    public bool IsLocked
    {
        get => _isLocked;
        set => _isLocked = value;
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!_isActive)
            {
                lock (ConsoleLock)
                {
                    Console.Clear();
                }
            }
        }
    }

    public void TogglePause()
    {
        _isPaused = !_isPaused;
        if (_isPaused)
        {
            lock (ConsoleLock)
            {
                Console.SetCursorPosition(0, _startingLineOffset);
                string msg = "  DASHBOARD PAUSED - PRESS ANY KEY TO RESUME  ";
                if (_useColor)
                {
                    msg = $"\e[41m\e[37m{msg}\e[0m";
                }
                Console.WriteLine(msg.PadRight(Console.WindowWidth));
            }
        }
    }

    public ConsoleStatusMonitor(IMessageBus bus, IFireLogger<ConsoleStatusMonitor> logger)
    {
        _startTime = DateTime.Now;
        var messageBus = bus ?? throw new ArgumentNullException(nameof(bus));
        _lines = 0;
        _errorLine = 30;
        _logger = logger;

        var config = DeviceSpace.Common.Configurations.ConfigurationLoader.GetSpaceConfig();
        _useColor = config?.ColorConsole ?? true;

        // Subscribe to all status messages.
        messageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleStatusMessageAsync);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // 140 columns gives us plenty of room for the 99 cells + labels
                int targetWidth = 250;
                int targetHeight = 60;

                // Cap to what the screen/OS actually supports
                targetWidth = Math.Min(targetWidth, Console.LargestWindowWidth);
                targetHeight = Math.Min(targetHeight, Console.LargestWindowHeight);

                // Set buffer first if we are expanding, to avoid window being larger than buffer.
                // Then set window. Finally set buffer to match window exactly if desired.
                if (targetWidth > 0 && targetHeight > 0)
                {
                    if (Console.BufferWidth < targetWidth) Console.BufferWidth = targetWidth;
                    if (Console.BufferHeight < targetHeight) Console.BufferHeight = targetHeight;

                    Console.WindowWidth = targetWidth;
                    Console.WindowHeight = targetHeight;

                    Console.BufferWidth = targetWidth;
                    Console.BufferHeight = targetHeight;
                }
            }
            catch
            {
                // Silently ignore failures to resize the console window.
                // This can happen in many terminal environments (VS Code, Windows Terminal, CI).
            }
        }
    }

    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    private string C(string ansiCode) => _useColor ? ansiCode : string.Empty;
    private string S(string unicode, string ascii) => _useColor ? unicode : ascii;

    private async Task HandleStatusMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (_isPaused || !_isActive) return;
        await Task.Run(() =>
        {
            if (message == null || !_isActive) return;
            
            if (message.Payload is not DeviceStatusMessage msg) return;
            var name = msg.DeviceId.DeviceName.ToUpper();
            var color = _useColor ? DeviceHealthExtension.ToAnsiColor(msg.Health) : string.Empty;
            var reset = _useColor ? DeviceHealthExtension.ToAnsiColor(null) : string.Empty;

            // Prepare the comment
            string raw = string.IsNullOrEmpty(msg.Comment) ? "" : $" - {msg.Comment}";
            string commentSuffix = raw.Length > 44 ? $"{raw.Substring(0, 43)}" : raw;
            string hbString = C("\e[30m") + S("♥ ", "  ") + C("\e[0m"); 
            if (msg.HbVisual != ' ')
            {
                // Active heartbeat -> Toggle between Bright Red and Blue based on the even/odd second
                hbString = msg.HbVisual == 'H' ? C("\e[34m") + S("♥ ", "  ") + C("\e[0m") : C("\e[31m") + S("♥ ", "* ") + C("\e[0m");
            }
            name = name.PadLeft(14, ' ');
            string formattedLine = $"{Truncate(name, 14),14}{hbString}{color}{Truncate(msg.State, 12),-12}";
            if (name.Contains("Manager", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            else
            {
                string div = C("\e[90m") + S("│", "|") + C("\e[0m"); // Dark gray vertical divider
              
                // Connections / Disconnects
                string cdString = $"{msg.CountConnections % 100,2}{C("\e[90m")}/{C("\e[0m")}{msg.CountDisconnects % 100,-2}";

                // Process Time
                var apt = Math.Round(msg.AvgProcessTime, 1);

                var aptColor = apt < 30 ? C("\e[92m") : (apt < 100 ? C("\e[93m") : C("\e[91m")); // Green/Yellow/Red

                var str = apt.ToString("000.0");
                string aptString = $"{aptColor}{S("⏱ ", "T ")}{str}{C("\e[0m")}";
                // I/O Messages 
                string ioString =
                    $"{C("\e[96m")}{S("↓", "v")}{C("\e[0m")}{msg.CountInbound % 10000,4}{C("\e[90m")}{S("│", "|")}{C("\e[0m")}{C("\e[36m")}{S("↑", "^")}{C("\e[0m")}{msg.CountOutbound % 10000,4}";

                // Errors (Green checkmark if 0, Red warning if > 0)
                string errString = msg.CountError > 0
                    ? $"{C("\e[91m")}{S("✖ ", "X ")}{msg.CountError,-2}{C("\e[0m")}"
                    : $"{C("\e[92m")}{S("✓ ", "O ")}0 {C("\e[0m")}";

                // Time Formatting
                var displayTime = msg.Timestamp.ToLocalTime().ToString("d/hh:mm:ss");
                // Build the final appended line (Assuming formattedLine already contains DeviceName and Status)
                formattedLine +=
                    $"{div}⇄ {cdString}{div}{aptString}{div}{ioString}{div}{errString} {div}{S("⌚", "T")}{displayTime,-11}{div}{commentSuffix,-43}{reset}";
            }

            int headerHeight = _useColor ? 1 : 2;
            int rowIndex = GetRowIndex(msg);
            int lineIndex = rowIndex + _startingLineOffset + headerHeight;

            lock (ConsoleLock)
            {
                WriteStatusLines(lineIndex, formattedLine);
                if (msg.Health != DeviceHealth.Critical) return;
                lock (ConsoleLock)
                {
                    WriteStatusLines(_errorLine, $"{DateTime.Now} CRITICAL [{name}]: {msg.Comment}");
                    _errorLine = _errorLine + 2;
                }

                if (_errorLine > START_ERROR + 10)
                    _errorLine = START_ERROR;
            }
        }, ct);
    }

    public void Start(CancellationToken cancellationToken, int startingLine)
    {
        _startingLineOffset = startingLine;
        if (_isPaused)
        {
            lock (ConsoleLock)
            {
                if (startingLine >= 0 && startingLine < Console.BufferHeight)
                {
                    Console.SetCursorPosition(0, startingLine);
                    string msg = "  DASHBOARD PAUSED - PRESS ANY KEY TO RESUME  ";
                    if (_useColor)
                    {
                        msg = $"\e[41m\e[37m{msg}\e[0m";
                    }
                    Console.WriteLine(msg.PadRight(Console.WindowWidth));
                }
            }
        }

        Task.Run(async () =>
        {
            Console.CursorVisible = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (_isPaused || !_isActive) 
                        {
                            await Task.Delay(500, cancellationToken);
                            continue;
                        }

                        lock (ConsoleLock)
                        {
                            // Ensure startingLine is within buffer bounds
                            if (startingLine >= 0 && startingLine < Console.BufferHeight)
                            {
                                Console.SetCursorPosition(0, startingLine);
                                // line 12,12,3,10,10,
                                if (_useColor)
                                {
                                    Console.WriteLine(
                                        $"\e[48;2;220;220;220m\e[38;2;0;90;190m {"Device Name",-10} \e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190mHB\e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190m {"Status",-9} \e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190mConnect\e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190mPsTm ms\e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190m↓IN /↑OUT  \e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190mError\e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190mLast Active  \e[38;2;0;0;0m│" +
                                        $"\e[38;2;0;90;190m Started:{_startTime:HH:mm:ss}      Now:{DateTime.Now:HH:mm:ss}    {(_isLocked ? "\e[91mLOCKED" : "\e[92mUNLOCKED")}\e[0m        ");
                                }
                                else
                                {
                                    Console.WriteLine(
                                        $" {"Device Name",-10} |" +
                                        $"HB|" +
                                        $" {"Status",-9} |" +
                                        $"Connect|" +
                                        $"PsTm ms|" +
                                        $"vIN /^OUT  |" +
                                        $"Error|" +
                                        $"Last Active  |" +
                                        $" Started:{_startTime:HH:mm:ss}      Now:{DateTime.Now:HH:mm:ss}    {(_isLocked ? "LOCKED" : "UNLOCKED")}        ");
                                    
                                    Console.WriteLine(new string('-', 120));
                                }
                            }
                            if (_lines == 0)
                                _lines = startingLine + (_useColor ? 1 : 2);
                        }
                    }
                    catch
                    {
                        // Ignore console I/O errors
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception)
            {
                _logger.Error("Status monitor task failed: {ex.Message}");
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }, cancellationToken);
    }

    private void WriteStatusLines(int index, string status)
    {
        try
        {
            lock (ConsoleLock)
            {
                int width = Console.WindowWidth > 0 ? Console.WindowWidth : 85;

                // Ensure index is within buffer bounds to prevent ArgumentOutOfRangeException
                if (index >= 0 && index < Console.BufferHeight)
                {
                    Console.SetCursorPosition(0, index);
                    // Pad to the window width to clear old content
                    int padWidth = Math.Max(0, width - 1);
                    Console.Write(status.PadRight(padWidth));

                    // Park the cursor at the bottom of the window to keep it out of the status area
                    int parkingRow = Math.Min(Console.WindowHeight - 1, Console.BufferHeight - 1);
                    Console.SetCursorPosition(0, parkingRow);
                }
            }
        }
        catch
        {
            // Ignore console I/O errors
        }
    }
}