using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Logging;
using Serilog;
using Workflow.PrintAndApplyFrc;

namespace Workflow.PrintAndApplyFrc;

public class PrintAndApplyFrc : WorkflowBase
{
    
    private class LabelWithTracking(LabelDataFrcMessage data, IEnumerable<string> printers)
    {
        public LabelDataFrcMessage Data { get; set; } = data;
        public HashSet<string> PendingPrinters { get; set; } = [..printers];
    }

    private readonly ConcurrentDictionary<string, PrinterStatus> _printerStatusStore = new();
    private readonly ConcurrentDictionary<string, LabelWithTracking> _labelStore = new();
    private List<string> _printTypes = [];
    private readonly Lock _lock = new Lock();
    private readonly ConcurrentDictionary<int, string> _expectedBarcodes = new();
    
    
    /// <summary>
    /// Represents the PrintAndApplyFrc workflow class, responsible for managing
    /// the printing and application of labels in a distribution or manufacturing system.
    /// Extends the WorkflowBase class to provide specific implementations for handling
    /// device status messages and coordinating label requests with printers.
    /// Subscribes to device status messages and initializes the printer status store
    /// upon instantiation.
    /// </summary>
    public PrintAndApplyFrc(IMessageBus messageBus, WorkflowConfig config, ILogger logger)
        : base(messageBus, config, logger)
    {
        MessageBus.SubscribeAsync(MessageBusTopic.DeviceStatus.ToString(), HandleStatusMessageAsync);

        if (IntitializePrinterStatusStore())
        {
            Logger.Information("[{Workflow}] Printer status store initialized with {Count} types.",
                WorkflowKey.DeviceName, _printTypes.Count);
        }
        else
        {
            Logger.Error("[{Workflow}] Failed to initialize Printer Status Store.", WorkflowKey.DeviceName);
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null // Keeps your casing exactly as defined
    };
    
    /// <summary>
    /// Initializes the printer status store by gathering printer configurations
    /// and creating a collection of unique printer types based on device settings.
    /// Iterates through all devices in the configuration, identifies those managed
    /// by "PrinterManager", and adds their status to the printer status store.
    /// </summary>
    /// <returns>True if the printer status store was successfully initialized; otherwise, false.</returns>
    private bool IntitializePrinterStatusStore()
    {
        var alldevices = ConfigurationLoader.GetAllDeviceConfig();

        foreach (var dev in alldevices)
        {
            if (dev is { Manager: "PrintClientManager", Enable: true })
            {
                var pType = ConfigurationLoader.GetOptionalConfig<string>(dev.Properties, "aSPrintType", "SHIPTOP");
                var pInduct = ConfigurationLoader.GetRequiredConfig<string>(dev.Properties, "Induct");
                var preferredGroup = ConfigurationLoader.GetOptionalConfig<int>(dev.Properties, "PreferredGroup", 1);

                if (pInduct != null)
                {
                    var tempStatus = new PrinterStatus(dev.Name, pType, pInduct, preferredGroup);

                    using (_lock.EnterScope())
                    {
                        _printerStatusStore[dev.Name] = tempStatus;
                    }
                }
            }
        }

        using (_lock.EnterScope())
        {
            _printTypes = _printerStatusStore.Values
                .Select(p => p.Type)
                .Distinct()
                .ToList();
        }

        return true;
    }

    /// <summary>
    /// Handles the logic for selecting printers based on the provided envelope data,
    /// updates the label store with the selected printers, and returns a new message envelope containing the result.
    /// </summary>
    /// <param name="envelope">The message envelope containing information used to decide which printers to use.</param>
    /// <param name="ct">A CancellationToken used to handle task cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains
    /// a new MessageEnvelope with updated printer selection data or null if the envelope data is invalid.</returns>
    public async Task<object?> HandlePrintersToUseAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        PublishStatusAsync();
        string payloadStr = envelope?.Payload?.ToString() ?? string.Empty;
        string gin = GetGinFromPayload(payloadStr);
        
        if (envelope?.Payload == null)
        {
            Logger.Error( "No payload received");
            return null;
        }
        
        var node = JsonNode.Parse(payloadStr);
        if (node == null)
        {
            Logger.Error( "ERROR payload not in JSON");
            return null;
        }
        
        var jsonObj = node.AsObject();
        var descPoint = jsonObj["DecisionPoint"]?.GetValue<string>();
        var parsedGin = jsonObj["GIN"]?.GetValue<int>();
        var bcNode = jsonObj["Barcodes"]?.AsArray();

        if (descPoint == null || parsedGin == null)
        {
            Logger.Error("No Decision Point or GIN found in payload");
            return null;
        }

        string? firstBarcode = (bcNode != null && bcNode.Count > 0)
            ? bcNode[0]?.GetValue<string>()
            : null;

        Logger.Debug($"Handling Printer Selection for Decision Point: {descPoint}", gin);

        var printers = await GetNextAvailablePrintersAsync(descPoint, ct);

        if (firstBarcode == null)
        {
            Logger.Warning($"Error No Barcode found in DReqM : {descPoint}.", gin);
            Tracker.IncrementError("No Barcode found in DReqM");
            return null;
        }

        if (printers.Count == 0)
        {
            Logger.Warning($"No printers available for Decision Point: {descPoint}.", gin);
            Tracker.IncrementError("Error No printers available for Decision Point");
            return null;
        }

        await UpdateLabelStoreAsync(firstBarcode, printers, ct);

        Logger.Debug($"Assigned to printers: {string.Join(", ", printers)}", gin);

        var payload = new { 
            MessageType = "DRespM", // Explicitly named for the PLC
            DecisionPoint = descPoint, 
            GIN = parsedGin, 
            Actions = printers 
        };

        var serializedPayload = JsonSerializer.Serialize(payload, _jsonOptions);

        return serializedPayload;
   }

    /// <summary>
    /// Handles the asynchronous processing of label data contained in the provided message envelope
    /// and stores it in a thread-safe manner. Updates the workflow status and logs the operation status.
    /// </summary>
    /// <param name="envelope">The message envelope containing the label data to be processed.</param>
    /// <param name="ct">A CancellationToken used to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, returning null when complete.</returns>
    private async Task<object?> HandleLabelToStorageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope == null) return null;
        Logger.Information( "Route -4 HandleLabelToStorageAsync  Handling Label Data for {Payload}", envelope.Payload);
        
        string payloadStr = envelope.Payload.ToString() ?? string.Empty;
        if (payloadStr.Length == 0) return null;
        payloadStr = payloadStr.Replace("LabelDataFrcMessage", "");
        if (string.IsNullOrEmpty(payloadStr) || ct.IsCancellationRequested)
        {
            Logger.Warning("HandleLabelToStorageAsync: Received empty payload or cancellation requested.");
            return null;
        }

        try
        {
            string json = payloadStr.ToJson();
            string bc = MessageParser.GetBarcodes(json).FirstOrDefault() ?? "999";
            string gin = MessageParser.GetGin(json).ToString();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            LabelDataFrcMessage? labelMsg = JsonSerializer.Deserialize<LabelDataFrcMessage>(json, options);

            if (labelMsg != null)
            {
                _labelStore.AddOrUpdate(
                    bc,
                    // Add: Create new tracking entry if it doesn't exist
                    new LabelWithTracking(labelMsg, new List<string>()),
                    // Update: Keep the existing PendingPrinters, but refresh the label data
                    (k, existingEntry) => new LabelWithTracking(labelMsg, existingEntry.PendingPrinters));

                Logger.Information("Label data stored for Barcode: {Barcode}", bc, gin);

                UpdateStatus(WorkflowState.Active, WorkflowEvent.MessageProcessed, DeviceHealth.Normal,
                    $"Stored label data for Barcode {bc}");
            }
             return "Label Stored Successfully";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to store label data for payload: {Payload}", payloadStr);
            UpdateStatus(WorkflowState.ActiveWithErrors, WorkflowEvent.Error, DeviceHealth.Warning, ex.Message);
            Tracker.IncrementError(ex.Message);
             return "Storing Label Failed";
        }
    }

    /// <summary>
    /// Handles the asynchronous processing of label data contained in the provided message envelope
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task<object?> HandleLabelVerificationToMqAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        // 1. Get the agnostic JSON payload
        string payloadStr = envelope?.Payload?.ToJson() ?? string.Empty;

        if (string.IsNullOrEmpty(payloadStr) || ct.IsCancellationRequested) return null;

        try
        {
            // 2. Extract current message data
            int gin = MessageParser.GetGin(payloadStr);
            string? sessionId = MessageParser.GetSession(payloadStr);
            List<string> barcodes = MessageParser.GetBarcodes(payloadStr);
            string scannedBarcode = barcodes.FirstOrDefault() ?? string.Empty;
            
            var printer = envelope?.Destination.DeviceName;
            // 3. Perform Lookup in your tracking dictionary
            if (_expectedBarcodes.TryGetValue(gin, out string? expected))
            {
                if (scannedBarcode.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Information("[Verification SUCCESS] GIN {GIN} matches expected barcode {Barcode}", gin, scannedBarcode);
                
                    // Optional: Trigger success logic or MQ message here
                    UpdateStatus(WorkflowState.Active, WorkflowEvent.MessageProcessed, DeviceHealth.Normal, $"Verified GIN {gin}");
                   
                    //var msg = FrcHelper.GetVerificationMessage( ) 
                }
                else
                {
                    Logger.Warning("[Verification MISMATCH] GIN {GIN} expected {Expected} but scanned {Actual}", 
                        gin, expected, scannedBarcode);
                
                    Tracker.IncrementError("Error Barcode Mismatch");
                    // Here you might want to return a "Reject" object to send to the PLC
                }
            }
            else
            {
                Logger.Warning("[Verification FAILED] No expected barcode found in memory for GIN {GIN}", gin);
                Tracker.IncrementError("Missing Expectation Data");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during Label Verification for GIN extraction.");
        }

        return null;
    }

    /// <summary>
    /// Handles the label request by parsing the provided message envelope payload to create a label request
    /// for a message queue. This operation involves extracting device details, decision points, and barcodes
    /// from the payload and constructing a new message envelope containing the label request.
    /// </summary>
    /// <param name="envelope">The message envelope containing the destination and payload data for processing.
    /// If null or invalid, the method will return null.</param>
    /// <param name="ct">Token used to cancel the operation, allowing for graceful termination when the operation
    /// is canceled.</param>
    /// <returns>Returns a new message envelope containing the label request if processing is successful;
    /// otherwise, returns null.</returns>
    private async Task<object?> HandleLabelRequestToMqAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload == null)
        {
            Logger.Error("No payload received for MQ Request");
            return null;
        }

        // 1. Get the payload as a string safely
        // If it's already a JsonElement or a raw JSON string, this works.
        string payloadStr = envelope.Payload is string s ? s : envelope.Payload.ToString();

        try 
        {
            var node = JsonNode.Parse(payloadStr);
            var jsonObj = node?.AsObject();
            if (jsonObj == null) return null;

            var plc = envelope.Destination.DeviceName;
        
            // Note: Check if your source uses "DecisionPoint" or "DecisionPoint" (case sensitive)
            var descPoint = jsonObj["DecisionPoint"]?.GetValue<string>() ?? "Unknown";

            // IMPORTANT: In your previous code, you called this "Actions". 
            // Ensure the incoming JSON matches "Barcodes"
            var barcodes = jsonObj["Barcodes"]?.AsArray()
                .Select(x => x?.GetValue<string>() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();

            // 2. Construct the record
            var labelRequest = new LabelRequestFrcMessage(
                Guid.NewGuid(),
                plc,
                descPoint,
                barcodes,
                new Characteristics("0", "0", "0", "0")
            );

            Logger.Debug("Generated LabelRequestFrcMessage for {PLC}, GIN: {GIN}", plc, envelope.Gin);

            return labelRequest;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to parse payload for MQ request");
            return null;
        }
    }

    /// <summary>
    /// Handles the processing of barcode data sent to a specific printer.
    /// Parses the barcode information from the message payload, retrieves the
    /// corresponding label data, and prepares a printer-specific message
    /// envelope for label printing. Additionally, logs relevant actions
    /// and updates the status in case of errors.
    /// </summary>
    /// <param name="envelope">The incoming message envelope containing barcode data and printer details.</param>
    /// <param name="ct">A cancellation token to monitor for cancellation requests.</param>
    /// <returns>A new message envelope for the printer with label data if successful; otherwise, null.</returns>
    private async Task<object?> HandleBarcodeToPrinterAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        string payload = envelope?.Payload?.ToString() ?? string.Empty;
        var data = ExtractRoutingData(payload);
        var gin = data.Gin.ToString();
        var barcode = data.FirstBarcode;
        var printer = data.DecisionPoint;

        
        Logger.Verbose($"parameter: {envelope}", gin);

        if (envelope?.Payload == null)
        {
            Logger.Error("Paylod = null");
            Tracker.IncrementError("No payload received");
            return null;
        }

        var result = await Task.Run(() =>
        {
            var labelData = RetrieveAndDecrement(barcode, printer);
            if (labelData == null)
            {
                Tracker.IncrementError("No payload received for printer:");
                return null;
            }

            Logger.Information($"Sending Label to printer : {barcode} at Printer: {printer}", gin);
            return  labelData;
                }, ct);
            string resulttypeStr = result.GetType().ToString();
            if (result is LabelDataFrcMessage labelDataObj)
        {
            _expectedBarcodes[data.Gin] = labelDataObj.GetExpectedScan();
        }
        else
        {
            Logger.Error("result is {} Payload is not a LabelDataFrcMessage");
            Tracker.IncrementError("Payload is not a LabelDataFrcMessage");
        }

        return result;
    }

    /// <summary>
    /// Extracts routing data from a JSON payload, providing information about the decision point,
    /// GIN (Goods Identification Number), and the first barcode in the payload.
    /// </summary>
    /// <param name="jsonPayload">The JSON string containing routing information.</param>
    /// <returns>
    /// A tuple containing the decision point (string), GIN (integer), and the first barcode (string).
    /// If the payload is invalid or properties are missing, the returned values will be their respective default values.
    /// </returns>
    public (string DecisionPoint, int Gin, string FirstBarcode) ExtractRoutingData(string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
            return (string.Empty, 0, string.Empty);

        try
        {
            var node = JsonNode.Parse(jsonPayload);
            if (node == null) return (string.Empty, 0, string.Empty);
            string decisionPoint = node["DecisionPoint"]?.ToString() ?? string.Empty;
            int gin = node["GIN"]?.GetValue<int>() ?? 0;

            var barcodesArray = node["Barcodes"]?.AsArray();
            string firstBarcode = (barcodesArray != null && barcodesArray.Count > 0)
                ? barcodesArray[0]?.ToString() ?? string.Empty
                : string.Empty;

            return (decisionPoint, gin, firstBarcode);
        }
        catch (Exception)
        {
            return (string.Empty, 0, string.Empty);
        }
    }

    /// <summary>
    /// Handles the processing of label data sent to a specific printer.
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task<object?> HandleContentToPrinterAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        string payload = envelope?.Payload?.ToString() ?? string.Empty;
        var data = ExtractRoutingData(payload);
        var gin = data.Gin.ToString();
        var barcode = data.FirstBarcode;
        var printer = data.DecisionPoint;

        Logger.Verbose($"parameter: {envelope}", gin);

        if (envelope?.Payload == null)
        {
            Tracker.IncrementError("No payload received");
            Logger.Error("No Payload");
            return null;
        }

        var result = await Task.Run(() =>
        {
            var labelData = RetrieveAndDecrement(barcode, printer);
            if (labelData == null)
            {
                Tracker.IncrementError("No payload received");
                return null;
            }

            Logger.Debug($"Sending Label to printer : {barcode} at Printer: {printer}", gin);
            return new MessageEnvelope(envelope.Destination, labelData);
        }, ct);


        return result;
    }

    /// <summary>
    /// Handles the processing of label data sent to a specific printer.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="printerName"></param>
    /// <returns></returns>
    private LabelDataFrcMessage? RetrieveAndDecrement(string key, string printerName)
    {
     
        Logger.Information("[{Method}] key: {key}, printerName: {printerName}" , "RetrieveAndDecrement", key, printerName);
        if (_labelStore.TryGetValue(key, out var tracker))
        {
            lock (tracker)
            {
                if (tracker.PendingPrinters.Contains(printerName))
                {
                    tracker.PendingPrinters.Remove(printerName);

                    Logger.Debug(
                        $"Printer {printerName} cleared for barcode {key}. Pending: {tracker.PendingPrinters.Count}");

                    if (tracker.PendingPrinters.Count == 0)
                    {
                        _labelStore.TryRemove(key, out _);
                        Logger.Verbose($"Barcode {key} fully processed and removed from store.");
                    }
                    return tracker.Data;
                }
            }
        }

        Logger.Warning($"Barcode {key} not found or printer {printerName} not authorized.");
        return null;
    }
    
    /// <summary>
    /// Handles the processing of label returned from queue
    /// </summary>
    /// <param name="barcode"></param>
    /// <param name="printers"></param>
    /// <param name="ct"></param>
    private async Task UpdateLabelStoreAsync(string barcode, List<string> printers, CancellationToken ct)
    {
        Logger.Verbose($"barcode: {barcode}, printerCount: {printers?.Count ?? 0}");

        _labelStore.AddOrUpdate(barcode,
            new LabelWithTracking(LabelDataFrcMessage.Empty, printers),
            (k, existing) => new LabelWithTracking(existing.Data, printers)
        );
        await Task.CompletedTask;
        
    }

    /// <summary>
    /// Asynchronously retrieves a list of the next available printers for the specified decision point.
    /// The method evaluates the current printer statuses, filters and selects the most suitable printers
    /// based on availability, last printed time, and their compatibility with the given decision point.
    /// Updates the last printed timestamp for selected printers.
    /// </summary>
    /// <param name="dPoint">The decision point for which available printers are being retrieved.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A list of printer names that are available and selected based on the provided decision point.</returns>
    private async Task<List<string>> GetNextAvailablePrintersAsync(string dPoint, CancellationToken ct = default)
    {
        Logger.Verbose($"decisionPoint: {dPoint}");

        var result = await Task.Run(() =>
        {
            List<string> selectedPrinters = new();

            using (_lock.EnterScope())
            {
                Logger.Debug($"Evaluating printers. Current status store size: {_printerStatusStore.Count}");

                var distinctTypes = _printerStatusStore.Values
                    .Select(p => p.Type)
                    .Distinct()
                    .ToList();

                foreach (var type in distinctTypes.TakeWhile(_ => !ct.IsCancellationRequested))
                {
                    var bestForType = _printerStatusStore.Values
                        .Where(p => p.Type == type && p.Induct == dPoint)
                        .OrderByDescending(p => p.IsAvailable)
                        .ThenBy(p => p.LastPrinted)
                        .ThenBy(p => p.Name)
                        .FirstOrDefault();

                    if (bestForType != null)
                    {
                        selectedPrinters.Add(bestForType.Name);
                        bestForType.LastPrinted = DateTime.UtcNow;
                    }
                }
            }

            return selectedPrinters;
        }, ct);
        
        return result;
    }

    /// <summary>
    /// Handles the processing of printer status messages received from the printers.
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="ct"></param>
    private async Task HandleStatusMessageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        string payloadStr = envelope?.Payload?.ToString() ?? string.Empty;


        if (envelope?.Destination.DeviceName == null)
        {
            Logger.Error("improper message received: no destination device name");
            return;
        }

        var printerName = envelope.Destination.DeviceName;

        try
        {
            DeviceStatusMessage? statusMsg = await Task.Run(() =>
            {
                if (envelope.Payload is DeviceStatusMessage msg) return msg;

                return string.IsNullOrEmpty(payloadStr)
                    ? null
                    : JsonSerializer.Deserialize<DeviceStatusMessage>(payloadStr);
            }, ct);

            if (statusMsg == null)
            {
                Logger.Error("Invalid status message received");
                return;
            }

            if (_printerStatusStore.TryGetValue(printerName, out var status))
            {
                var isNowAvailable = statusMsg.Health == DeviceHealth.Normal;

                if (status.IsAvailable != isNowAvailable)
                {
                    status.IsAvailable = isNowAvailable;
                    Logger.Debug($"Printer availability changed to: {isNowAvailable}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to parse status message ");
        }
    }
    
    /// <summary>
    /// Handles the processing of label data sent to a specific printer.
    /// </summary>
    /// <param name="plcName"></param>
    /// <param name="jsonString"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<LabelRequestFrcMessage?> ConvertGenericJsonToLabelRequestAsync(string plcName, string jsonString,
        CancellationToken ct = default)
    {
        string gin = GetGinFromPayload(jsonString);
      
        Logger.Verbose($"plcName: {plcName}");

        var result = await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var node = JsonNode.Parse(jsonString);
                if (node == null) return null;

                var jsonObj = node.AsObject();

                var barcodes = jsonObj["Barcodes"]?.AsArray()
                    .Select(b => b?.GetValue<string>() ?? string.Empty)
                    .ToList() ?? new List<string>();

                var characteristics = new Characteristics(
                    Height: jsonObj["Height"]?.GetValue<string>() ?? "0",
                    Length: jsonObj["Length"]?.GetValue<string>() ?? "0",
                    Width: jsonObj["Width"]?.GetValue<string>() ?? "0",
                    Weight: jsonObj["Weight"]?.GetValue<string>() ?? "0"
                );

              
                return new LabelRequestFrcMessage(
                    SessionId: Guid.NewGuid(),
                    ControllerId: plcName,
                    LineId: jsonObj["DecisionPoint"]?.GetValue<string>() ?? "Unknown",
                    Barcodes: barcodes,
                    Characteristics: characteristics
                );
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to convert generic JSON for {plcName}", gin);
                return null;
            }
        }, ct);

       
        return result;
    }

    /// <summary>
    /// Extracts the GIN from the provided payload.
    /// </summary>
    /// <param name="payload"></param>
    /// <returns></returns>
    private string GetGinFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "---";

        try
        {
            if (payload.TrimStart().StartsWith("{"))
            {
                var node = JsonNode.Parse(payload);
                return node?["GIN"]?.ToString() ?? "---";
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return "---";
    }
}