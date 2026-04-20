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

    // Thread-safe dictionary to store the latest status from the message bus
    private readonly DeviceLineRegistry _lineMap = new();
    private int _lines;
    private int _errorLine;
    private int _startingLineOffset = 0;
    private DateTime _startTime;
    private readonly IFireLogger _logger;

    public ConsoleStatusMonitor(IMessageBus bus, IFireLogger<ConsoleStatusMonitor> logger)
    {
        _startTime = DateTime.Now;
        var messageBus = bus ?? throw new ArgumentNullException(nameof(bus));
        _lines = 0;
        _errorLine = 30;
        _logger = logger;
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

    private async Task HandleStatusMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (message == null) return;
            
            if (message.Payload is not DeviceStatusMessage msg) return;
            var name = msg.DeviceId.DeviceName.ToUpper();
            var color = DeviceHealthExtension.ToAnsiColor(msg.Health);
            var reset = DeviceHealthExtension.ToAnsiColor(null);

            // Prepare the comment
            string raw = string.IsNullOrEmpty(msg.Comment) ? "" : $" - {msg.Comment}";
            string commentSuffix = raw.Length > 44 ? $"{raw.Substring(0, 43)}" : raw;
            string hbString = "\e[30m♥ \e[0m"; 
            if (msg.HbVisual != ' ')
            {
                // Active heartbeat -> Toggle between Bright Red and Blue based on the even/odd second
                hbString = msg.HbVisual == 'H' ? "\e[34m♥ \e[0m" : "\e[31m♥ \e[0m";
            }

            string formattedLine = $"{Truncate(name, 15),15}{hbString}{color}{Truncate(msg.State, 12),-12}";
            if (name.Contains("Manager", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            else
            {
                string div = "\e[90m│\e[0m"; // Dark gray vertical divider
              
                // Connections / Disconnects
                string cdString = $"{msg.CountConnections % 100,2}\e[90m/\e[0m{msg.CountDisconnects % 100,-2}";

                // Process Time
                var apt = Math.Round(msg.AvgProcessTime, 1);

                var aptColor = apt < 30 ? "\e[92m" : (apt < 100 ? "\e[93m" : "\e[91m"); // Green/Yellow/Red

                var str = apt.ToString("000.0");
                string aptString = $"{aptColor}⏱ {str}\e[0m";
                // I/O Messages 
                string ioString =
                    $"\e[96m↓\e[0m{msg.CountInbound % 10000,4}\e[90m│\e[0m\e[36m↑\e[0m{msg.CountOutbound % 10000,4}";

                // Errors (Green checkmark if 0, Red warning if > 0)
                string errString = msg.CountError > 0
                    ? $"\e[91m✖ {msg.CountError,-2}\e[0m"
                    : $"\e[92m✓ 0 \e[0m";

                // Time Formatting
                var displayTime = msg.Timestamp.ToLocalTime().ToString("d/hh:mm:ss");
                // Build the final appended line (Assuming formattedLine already contains DeviceName and Status)
                formattedLine +=
                    $"{div}⇄ {cdString}{div}{aptString}{div}{ioString}{div}{errString} {div}⌚{displayTime,-11}{div}{commentSuffix,-43}{reset}";
            }

            int lineIndex = 0;

            lock (ConsoleLock)
            {
                if (msg.ScreenIndex >= 0)
                {
                    lineIndex = msg.ScreenIndex + _startingLineOffset + 1;
                }
                else
                {
                    lineIndex = _startingLineOffset + 2;
                }
             
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
        Task.Run(async () =>
        {
            Console.CursorVisible = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        lock (ConsoleLock)
                        {
                            // Ensure startingLine is within buffer bounds
                            if (startingLine >= 0 && startingLine < Console.BufferHeight)
                            {
                                Console.SetCursorPosition(0, startingLine);
                                // line 12,12,3,10,10,
                                Console.WriteLine(
                                    $"\e[48;2;220;220;220m\e[38;2;0;90;190m {"Device Name",-10} \e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190mHB\e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190m {"Status",-9} \e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190mConnect\e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190mPsTm ms\e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190m↓IN /↑OUT  \e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190mError\e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190mLast Active  \e[38;2;0;0;0m│" +
                                    $"\e[38;2;0;90;190m Started:{_startTime:HH:mm:ss}      Now:{DateTime.Now:HH:mm:ss}        \e[0m");
                            }
                            if (_lines == 0)
                                _lines = startingLine + 1;
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
                    Console.WriteLine(status.PadRight(padWidth));
                }
            }
        }
        catch
        {
            // Ignore console I/O errors
        }
    }
}