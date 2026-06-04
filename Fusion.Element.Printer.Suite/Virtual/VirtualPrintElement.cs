using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Fusion.Element.Virtual.Printer;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.TCP_Classes;
using Serilog;
using Serilog.Core;

namespace Fusion.Element.Printer.Suite.Virtual;

public class VirtualPrintElement : TcpServerElementBase<PrintMessageProcessor>
{
    private bool _isPaperOut = false;
    private bool _isPaused = false;
    private bool _isHeadOpen = false;

    public VirtualPrintElement(IMessageBus bus, IElementBlueprint config, IFireLogger logger, LoggingLevelSwitch ls)
        : base(bus, config, logger, new PrintMessageProcessor(logger), ls,
            config.Properties.TryGetValue("Port", out var p) ? Convert.ToInt32(p) : 9100,
            terminalStr: new SequenceTerminationStrategy(
                Encoding.ASCII.GetBytes("~HS"),
                Encoding.ASCII.GetBytes("^XZ"),
                Encoding.ASCII.GetBytes(" </labels> ")),
            maxClients: 10)
    {
        Logger.Information($"ConfigName: {config.Name}");
        Processor.HeartbeatReceived += OnProcessorHBReceived;
        Processor.MessageReceived += OnProcessorMessageReceived;
        Processor.OnMessageError += OnProcessorMessageError;
    }

    private async Task OnProcessorMessageReceived(object arg)
    {
        if (arg is MessageEnvelope envelope)
        {
            // Fire the state machine event to increment the inbound message counter
            await Machine.FireAsync(Event.MessageReceived);
            
            string payload = envelope.Payload?.ToString() ?? string.Empty;
            ProcessLabelJob(payload);
        }
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

    protected override void OnSingleClientConnected()
    {
        base.OnSingleClientConnected();
        Logger.Information("[{Dev}] Printer client connected. Launching hardware simulation.", Config.Name);
        _ = Task.Run(() => RunPrinterSimulationAsync(ConnectionToken));
    }

    protected override void OnMultiClientConnected()
    {
        base.OnMultiClientConnected();
        Logger.Information("[{Dev}] Printer client connected (Multi). Launching hardware simulation.", Config.Name);
        _ = Task.Run(() => RunPrinterSimulationAsync(ConnectionToken));
    }


    /// <summary>
    /// Generates a Zebra ~HS (Host Status) compliant string.
    /// </summary>
    public string GenerateZebraStatus()
    {
        char paper = _isPaperOut ? '1' : '0';
        char head = _isHeadOpen ? '1' : '0';
        char pause = _isPaused ? '1' : '0';

        char stx = '\u0002';
        char etx = '\u0003';

        var sb = new StringBuilder();
        // Line 1: Basic status (Paper Out is field 3, index 2)
        sb.Append($"{stx}000,0,{paper},1234,000,0,0,0,000,0,0,0{etx}\r\n");
        // Line 2: Error status (Pause is field 4, index 3; Head is field 5, index 4)
        sb.Append($"{stx}001,0,0,{pause},{head},0,0,0,00000000,1,000{etx}\r\n");
        // Line 3: Memory/Option status
        sb.Append($"{stx}1234,0,0000,00000,00,0,0,0,000,000,000{etx}\r\n");

        return sb.ToString();
    }

    public async Task RunPrinterSimulationAsync(CancellationToken ct)
    {
        Logger.Information("[{Dev}] Printer simulation waiting for GIN 325 trigger.", Config.Name);

        try
        {
            // 1. Wait until GIN 325 is reached globally
            while (!SimulationCoordinator.Gin325Reached && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }

            if (ct.IsCancellationRequested) return;

            Logger.Information("[{Dev}] GIN 325 trigger detected. Starting hardware status swap cycle.", Config.Name);

            // Determine if this is Printer 1 or Printer 2 based on the name suffix
            bool isPrinter2 = Config.Name.EndsWith("152") || Config.Name.EndsWith("2");

            while (!ct.IsCancellationRequested)
            {
                double? elapsed = SimulationCoordinator.ElapsedSeconds;
                if (elapsed == null) { await Task.Delay(500, ct); continue; }

                // Reset statuses
                bool wasError = _isPaperOut || _isHeadOpen || _isPaused;
                _isPaperOut = false;
                _isHeadOpen = false;
                _isPaused = false;
                string comment = "ready";
                ElementHealth health = ElementHealth.Normal;

                if (elapsed < 10) // 0-10s: P1 Paper Out
                {
                    if (!isPrinter2) { _isPaperOut = true; comment = "out of paper"; health = ElementHealth.Warning; }
                }
                else if (elapsed < 20) // 10-20s: P2 Paper Out
                {
                    if (isPrinter2) { _isPaperOut = true; comment = "out of paper"; health = ElementHealth.Warning; }
                }
                else if (elapsed < 30) // 20-30s: P1 Head Open
                {
                    if (!isPrinter2) { _isHeadOpen = true; comment = "head open"; health = ElementHealth.Warning; }
                }
                else if (elapsed < 40) // 30-40s: P2 Head Open
                {
                    if (isPrinter2) { _isHeadOpen = true; comment = "head open"; health = ElementHealth.Warning; }
                }
                else if (elapsed < 50) // 40-50s: P1 Paused
                {
                    if (!isPrinter2) { _isPaused = true; comment = "paused"; health = ElementHealth.Warning; }
                }
                else if (elapsed < 60) // 50-60s: P2 Paused
                {
                    if (isPrinter2) { _isPaused = true; comment = "paused"; health = ElementHealth.Warning; }
                }

                bool isError = _isPaperOut || _isHeadOpen || _isPaused;
                if (isError != wasError || elapsed < 61) // Update status on change or during cycle
                {
                    UpdateStatus(Machine.State, isError ? Event.ServerError : Event.ServerStarted, health, comment);
                }

                if (elapsed >= 60)
                {
                    Logger.Information("[{Dev}] Hardware status simulation cycle complete. Remaining READY.", Config.Name);
                    break; 
                }

                await Task.Delay(500, ct);
            }
        }
        catch (TaskCanceledException)
        {
            Logger.Information("[{Dev}] Simulation task cancelled.", Config.Name);
        }
        finally
        {
            SetPaperStatus(false);
            SetPauseStatus(false);
            SetHeadStatus(false);
            UpdateStatus(Machine.State, Event.ServerStarted, ElementHealth.Normal, "ready");
        }
    }


    private void ProcessLabelJob(string zpl)
    {
        string gin = GetGinFromPayload(zpl);
        Tracker.Increment(ElementMetric.Labels);
        Logger.Information($"[{Config.Name}] Label Received: {zpl}");

        if (_isPaused || _isPaperOut || _isHeadOpen)
        {
            string reason = _isPaperOut ? "out of paper" : (_isPaused ? "paused" : "head open");
            Logger.Warning($"[{Config.Name}] Print Failed: {reason}", gin);
            Machine.Fire(Event.ServerError);
        }
        else
        {
            Logger.Information($"[{Config.Name}] Processing Label Job...", gin);
            Task.Delay(300).ContinueWith(_ => Logger.Debug($"[{Config.Name}] Job Printed Successfully.", gin));
        }
    }


    public void SimulateError(string errorType, bool active)
    {
        if (errorType == "Paper") _isPaperOut = active;
        else if (errorType == "Head") _isHeadOpen = active;
        else if (errorType == "Pause") _isPaused = active;

        string comment = active ? $"{errorType.ToLower()} error" : "ready";
        ElementHealth health = active ? ElementHealth.Warning : ElementHealth.Normal;
        
        UpdateStatus(Machine.State, active ? Event.ServerError : Event.ServerStarted, health, comment);
        
        Logger.Debug($"[{Config.Name}] Sensor Simulation: {errorType} is {(active ? "Active" : "Cleared")}");
    }


    protected override ElementHealth MapStateToHealth(State state)
    {
        if (_isPaperOut || _isHeadOpen || _isPaused) return ElementHealth.Warning;
        return base.MapStateToHealth(state);
    }

    private readonly StringBuilder _inputBuffer = new();

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