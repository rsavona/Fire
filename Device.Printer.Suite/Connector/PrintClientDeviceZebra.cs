using System.Net.Sockets;
using System.Text;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using Serilog.Core;
using ILogger = Serilog.ILogger;

namespace Device.Printer.Suite.Connector;

public enum PrinterStatus 
{
    // A value of 0 means no flags are set, indicating no errors.
    NoErrors = 0,

    // Assign powers of two to each error code.
    PaperOut = 1, 
    PrinterPaused = 2, 
    // Not in use, but reserved for future use.
    PrintBufferFull = 4, 
    DiagnosticMode = 8, 
    RamCorrupted = 16, 
    UnderTemperature = 32, 
    OverTemperature = 64, 
    PrinterHeadUp = 128,
    RibbonOut = 256, 
    UnknownError = 512,
    Disconnected = 1024, 
    Disabled = 2048 
}

/// <summary>
/// Is the interface to a zebra printer
/// Is a client and connects to a printer via TCP
/// </summary>
public class PrintClientDeviceZebra : TcpClientDeviceBase, IMessageProvider, ITcpPrintClientBase
{
    public string Brand { get; init; } = "Zebra";
    public PrintDestination DestinationType { get; init; }
    public bool PrintError { get; init; }
    public ZplString ErrorLabel { get; init; }
    public event Func<object, object, Task>? MessageReceived;

    // Property to track the physical state of the printer
    public PrinterStatus HardwareStatus { get; private set; } = PrinterStatus.NoErrors;


    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="config"></param>
    /// <param name="zebraLogger"></param>
    /// <param name="ls"></param>
    public PrintClientDeviceZebra(
        IDeviceConfig config,
        IFireLogger zebraLogger,
        LoggingLevelSwitch ls)
        : base(config, zebraLogger, ls, true)
    {
        // Initialize ZplString default if needed
        ErrorLabel = ZplString.CreateErrorLabel("Zebra Connection Error");
    }

    /// <summary>
    /// Starts the device and initiates the connection process.
    /// </summary>
    /// <param name="ct"></param>
    protected override async Task OnStartAsync(CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            while (Machine.State != State.Connected)
            {
                if (ct.IsCancellationRequested) return;

                await Task.Delay(1000, ct);

                Logger.Debug("[{Dev}] Still waiting for connection... Current State: {State}",
                    Config.Name, Machine.State);
            }

            Logger.Information("Connected to printer");

            await Task.Delay(10000, ct);
        }
    }


    protected override bool IsHeartbeat(string incomingData)
    {
        _ = ParseStatusAsync(incomingData);
        return true;
    }


    private async Task ParseStatusAsync(string incomingData)
    {
        if (string.IsNullOrWhiteSpace(incomingData))
        {
            Logger.Warning($"[StatusErr] {Config.Name}: Incoming data is null or empty.");
            return;
        }

        var printStatusString = incomingData
            .Replace((char)3, ';')
            .Replace((char)4, ';')
            .Replace('\r', ';')
            .Replace('\n', ';');

        var sanitizedData =
            new string(incomingData.Where(c => char.IsLetterOrDigit(c) || c == ',' || c == '^').ToArray());
        sanitizedData = sanitizedData.TrimEnd(',');

        var statusArray = sanitizedData.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        if (statusArray.Length < 22)
        {
            Logger.Information($"[StatusErr] {Config.Name}: Raw: '{incomingData}'");
            return;
        }

        char stx = '\u0002';
        string[] stxBlocks = printStatusString.Split(new[] { stx }, StringSplitOptions.RemoveEmptyEntries);

        PrinterStatus determinedStatus = PrinterStatus.NoErrors;

        if (stxBlocks.Length > 0)
        {
            string[] parts = stxBlocks[0].Split(',');

            // Check for physical hardware issues
            if (parts.Length > 3)
            {
                if (parts[2] == "1") determinedStatus = PrinterStatus.PaperOut;
                else if (parts[3] == "1") determinedStatus = PrinterStatus.PrinterPaused;
            }
        }

        Logger.Information($"[StatusChk] {Config.Name}: Printer reports {determinedStatus}.");
        if (determinedStatus != HardwareStatus)
        {
            HardwareStatus = determinedStatus;
            bool hasHardwareError = HardwareStatus == PrinterStatus.PaperOut ||
                                    HardwareStatus == PrinterStatus.PrinterPaused;

            if (hasHardwareError)
            {
                if (Machine.CanFire(Event.MakeUnavailable))
                {
                    await Machine.FireAsync(Event.MakeUnavailable);
                }
            }
            else if (HardwareStatus == PrinterStatus.NoErrors)
            {
                Logger.Information("[{Dev}] Hardware issue cleared. Resuming operations.", Config.Name);

                // Operator fixed the issue; pull it out of the Unavailable substate
                if (Machine.CanFire(Event.MakeAvailable))
                {
                    await Machine.FireAsync(Event.MakeAvailable);
                }
            }
        }
        Tracker.HeartBeat();
        UpdateAndNotify();
    }
    

    protected override string GetHeartbeatMessage()
    {
        return "~HS";
    }

    /// <summary>
    /// Sends ZPL data over the persistent TCP stream.
    /// </summary>
    public async Task PrintAsync(string labelData)
    {
        // Ensure we are in a connected state before attempting to write
        if (Machine.State is not State.Connected)
        {
            Logger.Warning("[{Dev}] Print ignored: Device is in state {State}", Config.Name, Machine.State);
            return;
        }
        try
        {
            byte[] data = Encoding.ASCII.GetBytes(labelData);

            // Get the stream from the base TcpClientDeviceBase
            NetworkStream? stream = GetStream();
            if (stream != null && stream.CanWrite)
            {
                Logger.Information("[{Dev}] Sending Print Job. Session: {SessionId}", Config.Name, labelData);

                await stream.WriteAsync(data, 0, data.Length);
                await stream.FlushAsync();

                Tracker.IncrementOutbound();
                Logger.Verbose("[{Dev}] ZPL Sent: {Zpl}", Config.Name, labelData);
            }
            else
            {
                Logger.Error("[{Dev}] Stream is unavailable or unwritable.", Config.Name);
                await Machine.FireAsync(Event.Error); // Trigger reconnection logic in base
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Critical error during PrintAsync for label {label}", Config.Name,
                labelData);
        }
    }

    protected override Task HandleReceivedDataAsync(string incomingData)
    {
        Logger.Debug("[{Dev}] Received from printer: {Data}", Config.Name, incomingData);
        return Task.CompletedTask;
    }

    public override Task SendHeartbeatAsync(CancellationToken token = default)
    {
        try
        {
            return SendAsync(GetHeartbeatMessage(), token, false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Error sending heartbeat: {Message}", Config.Name, ex.Message);
            return Task.CompletedTask;
        }
    }
}