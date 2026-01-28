using System.Collections.Concurrent;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;

namespace DeviceSpaceConsole;

public class ConsoleStatusMonitor
{
    private static readonly Lock ConsoleLock = new();

    private const int START_ERROR = 30;

    // Thread-safe dictionary to store the latest status from the message bus
    private readonly ConcurrentDictionary<string, int> _lineMap = new();
    private int _lines;
    private int _errorLine;
    private DateTime _startTime;

    public ConsoleStatusMonitor(IMessageBus bus)
    {
        _startTime =  DateTime.Now;
        var messageBus = bus ?? throw new ArgumentNullException(nameof(bus));
        _lines = 0;
        _errorLine = 30;
        // Subscribe to all status messages.
        messageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleStatusMessageAsync);
        if (OperatingSystem.IsWindows())
        {
            // 140 columns gives us plenty of room for the 99 cells + labels
            Console.WindowWidth = 250;
            Console.WindowHeight = 60;
            Console.BufferWidth = 250;
            Console.BufferHeight = 60;
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
            string commentSuffix = raw.Length > 35 ? $"{raw.Substring(0, 33)}..." : raw;
            string formattedLine = $"{Truncate(name,12),-12}{color}{Truncate(msg.State,12),-12}";

            if (name.Contains("Manager", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            else
            {
                TimeSpan cleanTime = new TimeSpan(msg.Timestamp.TimeOfDay.Hours, msg.Timestamp.TimeOfDay.Minutes,
                    msg.Timestamp.TimeOfDay.Seconds);
                var hbString = "   ";
                if (msg.HbVisual != ' ')
                    hbString = $"({msg.HbVisual})";
                formattedLine += $"{hbString}C/D:{msg.CountConnections % 100,2}/{msg.CountDisconnects % 100,-2} APT:{msg.AvgProcessTime,-5} I/O:{msg.CountInbound % 1000,3}/{msg.CountOutbound % 1000,-3} Er:{msg.CountError,-2}  {cleanTime.ToString(),-9} {commentSuffix,-40}{reset}";
            }
            

            int lineIndex;
            lock (ConsoleLock)
            {
                if (!_lineMap.TryGetValue(name, out lineIndex))
                {
                    _lines = _lines + 1;
                    _errorLine = _lines + 2;
                    lineIndex = _lines;
                    _lineMap[name] = lineIndex;
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
        Task.Run(async () =>
        {
            Console.CursorVisible = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (ConsoleLock)
                    {
                        // Reset _lines occasionally or just ensure header is printed
                        Console.SetCursorPosition(0, startingLine);
                                // line 12,12,3,10,10,
                                Console.WriteLine(
                                    $"\x1b[48;2;220;220;220m\x1b[38;2;0;90;190mDevice Name \x1b[38;2;0;0;0m|\x1b[38;2;0;90;190m   Status  \x1b[38;2;0;0;0m|\x1b[38;2;0;90;190mHB\x1b[38;2;0;0;0m|\x1b[38;2;0;90;190m Con/Dis \x1b[38;2;0;0;0m|\x1b[38;2;0;90;190mProcTime \x1b[38;2;0;0;0m|\x1b[38;2;0;90;190mMsgs IN/OUT\x1b[38;2;0;0;0m|\x1b[38;2;0;90;190mErrors\x1b[38;2;0;0;0m|\x1b[38;2;0;90;190mLast Active\x1b[38;2;0;0;0m| Started:{_startTime:T} Now:{DateTime.Now:T} \x1b[0m");
                        if (_lines == 0)
                            _lines = startingLine + 1;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Clear();
                Console.WriteLine($"Status monitor task failed: {ex.Message}");
            }
            finally
            {
                Console.CursorVisible = true;
                Console.Clear();
                Console.WriteLine("`Status monitor stopped.");
            }
        }, cancellationToken);
    }

    private void WriteStatusLines(int index, string status)
    {
        lock (ConsoleLock)
        {
            int width = Console.WindowWidth > 0 ? Console.WindowWidth : 85;

            Console.SetCursorPosition(0, index);
            // Prevent crash if window is too small
            int padWidth = Math.Max(0, 85);
            Console.WriteLine(status.PadRight(padWidth));
        }
    }
}