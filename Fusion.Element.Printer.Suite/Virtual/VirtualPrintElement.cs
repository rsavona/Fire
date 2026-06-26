using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Timers;
using System.Xml.Linq;
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
                Encoding.ASCII.GetBytes("</labels>")),
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
            Logger.Information($"[{Config.Name}] Raw Label Data Received: {payload}");
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
        Logger.Information("[{Dev}] Printer hardware simulation waiting for fault trigger.", Config.Name);

        try
        {
            // 1. Wait until GIN 325 is reached globally
            while (!SimulationCoordinator.Gin325Reached && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }

            if (ct.IsCancellationRequested) return;

            Logger.Information("[{Dev}] Hardware status simulation trigger detected. Starting hardware status swap cycle.", Config.Name);

            // Determine if this is Printer 1 or Printer 2 based on the name suffix
            bool isPrinter2 = Config.Name.EndsWith("152") || Config.Name.EndsWith("2");
            bool wasError = false;

            while (!ct.IsCancellationRequested)
            {
                double? elapsed = SimulationCoordinator.ElapsedSeconds;
                if (elapsed == null) { await Task.Delay(500, ct); continue; }

                // Get status from coordinator
                var (isFaulted, reason) = SimulationCoordinator.GetSimulatedHardwareStatus(Config.Name, elapsed);
                
                // Update local status flags
                _isPaperOut = reason == "out of paper";
                _isHeadOpen = reason == "head open";
                _isPaused = reason == "paused";

                if (isFaulted != wasError || elapsed < 61)
                {
                    ElementHealth health = isFaulted ? ElementHealth.Warning : ElementHealth.Normal;
                    UpdateStatus(Machine.State, isFaulted ? Event.ServerError : Event.ServerStarted, health, reason);
                    wasError = isFaulted;
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


    private void ProcessLabelJob(string payload)
    {
        // 1. Identify and count all labels in this payload
        // We count ZPL start tags (^XA) and XML label tags (<label)
        int labelCount = 0;
        
        // Count ^XA (Case-insensitive just in case, though standard is uppercase)
        int zplIndex = 0;
        while ((zplIndex = payload.IndexOf("^XA", zplIndex, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            labelCount++;
            zplIndex += 3;
        }

        // Count <label (Case-insensitive to handle various XML styles)
        int xmlIndex = 0;
        while ((xmlIndex = payload.IndexOf("<label", xmlIndex, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            // Avoid double counting if someone uses <labels><label>...
            // We look for the start of an actual label element
            labelCount++;
            xmlIndex += 6;
        }

        // Fallback: If no tags found but we got here, it's at least one blob of data
        if (labelCount == 0 && !string.IsNullOrWhiteSpace(payload)) labelCount = 1;

        for (int i = 0; i < labelCount; i++)
        {
            Tracker.Increment(ElementMetric.Labels);
        }

        var identity = GetPrintIdentity(payload);
        Logger.Information(
            "[{Dev}] Label(s) Received: {LabelCount} in block. {PrintIdentity}",
            Config.Name, labelCount, FormatPrintIdentity(identity));
        
        // Log the full payload for debugging if it's not too huge
        if (payload.Length < 1000)
        {
            Logger.Debug($"[{Config.Name}] Raw Payload: {payload}");
        }

        if (_isPaused || _isPaperOut || _isHeadOpen)
        {
            string reason = _isPaperOut ? "out of paper" : (_isPaused ? "paused" : "head open");
            Logger.Warning(
                "[{Dev}] Print Failed: {Reason}. {PrintIdentity}",
                Config.Name, reason, FormatPrintIdentity(identity));
        
        }
        else
        {
            Logger.Information(
                "[{Dev}] Processing {LabelCount} Label(s). {PrintIdentity}",
                Config.Name, labelCount, FormatPrintIdentity(identity));
            Task.Delay(300).ContinueWith(_ => Logger.Debug(
                "[{Dev}] Job Printed Successfully. {PrintIdentity}",
                Config.Name, FormatPrintIdentity(identity)));
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

    private static (string? Gin, string? Barcode) GetPrintIdentity(string? payload)
    {
        var gin = ExtractGinFromPayload(payload);
        var barcode = ExtractBarcodeFromPayload(payload);

        if (string.IsNullOrWhiteSpace(gin) &&
            SimulationCoordinator.TryGetGinForBarcode(barcode, out var resolvedGin))
        {
            gin = resolvedGin.ToString();
        }

        return (gin, barcode);
    }

    private static string FormatPrintIdentity((string? Gin, string? Barcode) identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.Gin))
        {
            return $"GIN: {identity.Gin}";
        }

        if (!string.IsNullOrWhiteSpace(identity.Barcode))
        {
            return $"Barcode: {identity.Barcode}";
        }

        return "GIN: --- Barcode: ---";
    }

    private static string? ExtractGinFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                var node = JsonNode.Parse(payload);
                var jsonGin = FindJsonValue(node, "GIN", "gin");
                if (!string.IsNullOrWhiteSpace(jsonGin))
                {
                    return jsonGin;
                }
            }
        }
        catch
        {
        }

        try
        {
            var match = Regex.Match(payload, @"(?i)(?:\bGIN\b|name\s*=\s*[""']GIN[""'])\D{0,20}(\d+)");
            if (match.Success && match.Groups.Count > 1) return match.Groups[1].Value;
        }
        catch
        {
        }

        return null;
    }

    private static string? ExtractBarcodeFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                var node = JsonNode.Parse(payload);
                var barcode = FindJsonValue(node, "LPN", "lpn", "barcode", "Barcode", "printedBarcode", "barcodes", "Barcodes");
                if (!string.IsNullOrWhiteSpace(barcode))
                {
                    return barcode;
                }
            }
        }
        catch
        {
        }

        var xmlBarcode = ExtractBarcodeFromXml(payload);
        if (!string.IsNullOrWhiteSpace(xmlBarcode))
        {
            return xmlBarcode;
        }

        var zplBarcode = ExtractBarcodeFromZpl(payload);
        if (!string.IsNullOrWhiteSpace(zplBarcode))
        {
            return zplBarcode;
        }

        return null;
    }

    private static string? ExtractBarcodeFromXml(string payload)
    {
        var preferredNames = new[] { "LPN", "lpn", "barcode", "Barcode", "printedBarcode" };

        try
        {
            var doc = XDocument.Parse(payload);
            foreach (var name in preferredNames)
            {
                var value = doc
                    .Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "variable", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
                    ?.Value;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }
        catch
        {
        }

        foreach (var name in preferredNames)
        {
            var match = Regex.Match(
                payload,
                $@"<variable\s+[^>]*name=[""']{Regex.Escape(name)}[""'][^>]*>(?<value>.*?)</variable>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
            {
                var value = match.Groups["value"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ExtractBarcodeFromZpl(string payload)
    {
        foreach (Match match in Regex.Matches(payload, @"\^FD(?<value>[^^\r\n]+?)\^FS", RegexOptions.IgnoreCase))
        {
            var value = match.Groups["value"].Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var labeledValue = Regex.Match(value, @"(?i)^(?:LPN|BARCODE|BC)\s*[:=]\s*(?<barcode>.+)$");
            if (labeledValue.Success)
            {
                value = labeledValue.Groups["barcode"].Value.Trim();
            }

            if (value.Any(char.IsLetterOrDigit))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindJsonValue(JsonNode? node, params string[] propertyNames)
    {
        if (node == null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!obj.TryGetPropertyValue(propertyName, out var value) || value == null)
                {
                    continue;
                }

                if (value is JsonArray arr)
                {
                    var first = arr.FirstOrDefault()?.ToString();
                    if (!string.IsNullOrWhiteSpace(first))
                    {
                        return first;
                    }
                }
                else
                {
                    var scalar = value.ToString();
                    if (!string.IsNullOrWhiteSpace(scalar))
                    {
                        return scalar;
                    }
                }
            }

            foreach (var child in obj.Select(kvp => kvp.Value))
            {
                var match = FindJsonValue(child, propertyNames);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                var match = FindJsonValue(child, propertyNames);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }

        return null;
    }
}
