using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Device.Virtual.Printer;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.TCP_Classes;
using Serilog;
using Serilog.Core;

namespace Device.Printer.Suite.Virtual;

public class VirtualPrintDevice : TcpServerDeviceBase<PrintMessageProcessor>
{
    private bool _isPaperOut = false;
    private bool _isPaused = false;
    private bool _isHeadOpen = false;

    
    public int PeriodicCheckTimeoutMs { get; set; }

    public VirtualPrintDevice(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(config, logger, new PrintMessageProcessor(logger), ls,
            config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 9100,
            terminalStr: new SequenceTerminationStrategy(
                Encoding.ASCII.GetBytes("~HS"),
                Encoding.ASCII.GetBytes("^ZX")),
            maxClients: 1)
    {
        Logger.Debug($"ConfigName: {config.Name}");
        Processor.HeartbeatReceived += OnProcessorHBReceived;
        Processor.MessageReceived += OnProcessorMessageReceived;
        Processor.OnMessageError += OnProcessorMessageError;
    }

       private void OnProcessorHBReceived(string client)
    {
         Tracker.HeartBeat();
        UpdateAndNotify();
        _ = SendAsync(client, GenerateZebraStatus());
    }
       
   
    private void OnProcessorMessageError(string errorMessage)
    {
        OnError("Protocol", new Exception(errorMessage));
    }

    private void OnProcessorMessageReceived(object message)
    {
    }

    public void SetPaperStatus(bool isOut)
    {
        _isPaperOut = isOut;
    }

    public void SetHeadStatus(bool isOpen)
    {
        _isHeadOpen = isOpen;
    }

    public void SetPauseStatus(bool isPaused)
    {
        _isPaused = isPaused;
    }

    protected override void OnProcessorHeartbeatReceived(string id)
    {
        _ = SendAsync(id, GenerateZebraStatus());
    }


    /// <summary>
    /// Generates a Zebra ~HS (Host Status) compliant string.
    /// </summary>
    public string GenerateZebraStatus()
    {
        char paper = _isPaperOut ? '1' : '0';
        char head = _isHeadOpen ? '1' : '0';
        char pause = _isPaused ? '1' : '0';

        var sb = new StringBuilder();
        // Line 1: Basic status
        sb.Append("123,0,0,1234,000,0,0,0,000,0,0,0\r\n");
        // Line 2: Error status (Pause, Head, Paper)
        sb.Append($"001,0,0,{pause},{head},{paper},0,0,00000000,1,000\r\n");
        // Line 3: Memory/Option status
        sb.Append("1234,0,0000,00000,00,0,0,0,000,000,000\r\n");

        var result = sb.ToString();
        return result;
    }

    public async Task RunPrinterSimulationAsync(CancellationToken ct)
{
    Logger.Information("Starting Deterministic Zebra Printer Simulation.");

    try
    {
        // 1. 30 sec: Paper Out
        Logger.Warning("SIMULATION: Triggered Paper Out for 30 seconds.");
        SetPaperStatus(true);
        SetPauseStatus(false);
        SetHeadStatus(false);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // 2. 30 sec: No Error
        Logger.Information("SIMULATION: Clearing Paper Out. No errors for 30 seconds.");
        SetPaperStatus(false);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // 3. 30 sec: Paused
        Logger.Warning("SIMULATION: Triggered Paused for 30 seconds.");
        SetPauseStatus(true);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // 4. 30 sec: No Error
        Logger.Information("SIMULATION: Clearing Paused. No errors for 30 seconds.");
        SetPauseStatus(false);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // 5. 300 sec: Head Open
        Logger.Warning("SIMULATION: Triggered Head Open for 300 seconds (5 minutes).");
        SetHeadStatus(true);
        await Task.Delay(TimeSpan.FromSeconds(300), ct);

        // 6. Indefinite: No Errors
        Logger.Information("SIMULATION: Clearing Head Open. System returning to normal indefinitely.");
        SetHeadStatus(false);

        // Hold this state forever (until the application is stopped and the token cancels)
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
    catch (TaskCanceledException)
    {
        // Expected behavior when the service shuts down
        Logger.Information("SIMULATION: Simulation task cancelled. Shutting down.");
    }
    finally
    {
        // Ensure the printer is left in a clean state if the task exits early
        SetPaperStatus(false);
        SetPauseStatus(false);
        SetHeadStatus(false);
    }
}


    protected void OnServerMessageReceived(string clientKey, string payload, DateTime timestamp)
    {
        Logger.Information($"Message IN");

        if (payload != null && payload.Contains("~HS", StringComparison.OrdinalIgnoreCase))
        {
            var responsePayload = GenerateZebraStatus();
            Logger.Information(
                $"Message OUT (Amount: {responsePayload.Length} chars) to {clientKey}: {responsePayload.TrimEnd()}");
            _ = SendAsync(clientKey, responsePayload);
        }

        if (payload != null && payload.Contains("^XA", StringComparison.OrdinalIgnoreCase))
        {
            ProcessLabelJob(payload);
        }
    }

    private void ProcessLabelJob(string zpl)
    {
        string gin = GetGinFromPayload(zpl);

        if (_isPaused || _isPaperOut || _isHeadOpen)
        {
            Logger.Warning($"[{Config.Name}] Print Failed: Hardware error state active.", gin);
            Machine.Fire(Event.ServerError);
        }
        else
        {
            Logger.Information($"[{Config.Name}] Processing Label Job...", gin);
            Task.Delay(300).ContinueWith(_ => Logger.Debug($"[{Config.Name}] Job Printed Successfully.", gin));
        }
    }


    protected void OnPeriodicCheck(object? sender, ElapsedEventArgs e)
    {
        // No need for LogEnter/Exit here to avoid flooding logs every second
        if (ConnectedClients.IsEmpty) return;

        var now = DateTime.Now;
        foreach (var client in ConnectedClients)
        {
            var elapsed = now - client.Value;
            if (elapsed.TotalMilliseconds > PeriodicCheckTimeoutMs)
            {
                Logger.Warning(
                    $"[{Config.Name}] Watchdog: Client {client.Key} timed out after {elapsed.TotalSeconds:F1}s.");

                // Disconnect the client at the server level
                Server.DisconnectClient(client.Key);

                // The Server.ClientConnectionChanged will fire, 
                // which eventually calls OnClientDisconnected in the state machine.
            }
        }
    }


    public void SimulateError(string errorType, bool active)
    {
        if (errorType == "Paper") _isPaperOut = active;
        if (errorType == "Head") _isHeadOpen = active;
        _isPaused = active;

        Machine.Fire(Event.ServerError);
        Logger.Debug($"[{Config.Name}] Sensor Simulation: {errorType} is {(active ? "Active" : "Cleared")}");
    }


    protected override DeviceHealth MapStateToHealth(State state)
    {
        DeviceHealth result = (_isPaperOut || _isHeadOpen) ? DeviceHealth.Warning : base.MapStateToHealth(state);

        return result;
    }

    private bool _isPrinting = false;
    private readonly StringBuilder _inputBuffer = new();

    protected async Task HandleReceivedDataAsync(string incomingData)
    {
        // 1. Branch for ~HS (Host Status) Query
        if (incomingData.Contains("~HS"))
        {
            //await SendZebraStatusResponse();
            if (incomingData.Trim() == "~HS") return;
        }

        // 2. Buffer the incoming ZPL data
        _inputBuffer.Append(incomingData);
        string currentContent = _inputBuffer.ToString();

        // 3. Branch for ^XZ (Print Command)
        if (currentContent.Contains("^XZ"))
        {
            _inputBuffer.Clear();
            Logger.Information("Label received (^XZ). Starting physical print simulation...");

            // Fire and forget the print simulation task
            _ = SimulatePrintJobAsync();
        }
    }

    private async Task SimulatePrintJobAsync()
    {
        _isPrinting = true;

        // Simulate the mechanical time it takes to print a 4x6 label (e.g., 1.5 seconds)
        await Task.Delay(1500);

        _isPrinting = false;
        Logger.Debug("Print job complete. Buffer cleared.");
    }

    private string GetGinFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "---";
        try
        {
            var match = Regex.Match(payload, @"GIN:?\s*(\d+)");
            if (match.Success && match.Groups.Count > 1) return match.Groups[1].Value;
        }
        catch
        {
        }

        return "---";
    }
}