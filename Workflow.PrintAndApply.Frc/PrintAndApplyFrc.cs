using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.Logging;
using Serilog;

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
                var tempStatus = new PrinterStatus(dev.Name, pType, pInduct, preferredGroup);

                using (_lock.EnterScope())
                {
                    _printerStatusStore[dev.Name] = tempStatus;
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
        Logger.Verbose("[{Workflow}] Handling Printer Selection for envelope: {Dest}",
            "HandlePrintersToUseAsync", envelope.Destination);
        
        // 1. Parsing - Make the payload safe to use as a JSON object
        var node = JsonNode.Parse(envelope.Payload.ToString() ?? "{}");
        if (node == null) return null;
        var jsonObj = node.AsObject();

        var descPoint =         jsonObj["DecisionPoint"]?.GetValue<string>();
        var gin =               jsonObj["GIN"]?.GetValue<int>();
        var bcNode=    jsonObj["Barcodes"]?.AsArray();

        if (descPoint == null || gin == null) return null;

        string? firstBarcode = (bcNode != null && bcNode.Count > 0)
            ? bcNode[0]?.GetValue<string>()
            : null;

        Logger.Debug("[{Workflow}] Handling Printer Selection for Decision Point: {DecisionPoint} and GIN: {GIN}",
            null);
        // 2. Await the logic that fetches printer status (WCS Logic)
        var printers = await GetNextAvailablePrintersAsync(descPoint, ct);

        if (firstBarcode == null)
        {
              Logger.Warning("[{Workflow}] No Barcode found in DReqM : {DecisionPoint}.",
                "HandlePrintersToUseAsync", descPoint);
              Tracker.IncrementError("No Barcode found in DReqM");
              return null;
        }
        if (printers.Count == 0)
        {
            Logger.Warning("[{Workflow}] No printers available for Decision Point: {DecisionPoint}.",
                "HandlePrintersToUseAsync", descPoint);
            Tracker.IncrementError("No printers available for Decision Point");
            return null;
        }
        
        //  Sending these to the MQ, this is a running list of what is expected to comeback  
        await UpdateLabelStoreAsync(firstBarcode, printers, ct);
        
        Logger.Debug("[{Workflow}] GIN {Gin} assigned to printers: {Printers}",
                "HandlePrintersToUseAsync", gin, printers);
        
        var payload = new { DecisionPoint = descPoint, GIN = gin, Actions = printers , Type = "MRespM"};
        // 4. Wrap return in a Task (already handled by the method signature)
        return JsonSerializer.Serialize(payload);
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
        // Use the CancellationToken to check if the service is shutting down
        if (envelope?.Payload == null || ct.IsCancellationRequested) return null;

        try
        {
            await Task.Run(() =>
            {
                var payloadString = envelope.Payload?.ToString() ?? "{}";
                var data = JsonSerializer.Deserialize<LabelDataFrcMessage>(payloadString);

                if (data?.Barcodes is { Count: > 0 })
                {
                    var key = data.Barcodes[0];

                    // Perform the thread-safe update
                    _labelStore.AddOrUpdate(key,
                        new LabelWithTracking(data, new List<string>()),
                        (k, existingEntry) => new LabelWithTracking(data, existingEntry.PendingPrinters));

                    Logger.Information("[{Workflow}] Label data stored for Barcode: {BC}",
                        "HandleLabelToStorageAsync", key);

                    // Update the health/status
                    UpdateStatus(WorkflowState.Active, WorkflowEvent.MessageProcessed, DeviceHealth.Normal,
                        $"Stored label data for Barcode {key}");
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[{Workflow}] Storage operation was cancelled.", "HandleLabelToStorageAsync");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Workflow}] Failed to store label data.", "HandleLabelToStorageAsync");
            // Ensure error status updates are also handled safely
            UpdateStatus(WorkflowState.ActiveWithErrors, WorkflowEvent.Error, DeviceHealth.Warning,
                $"Failed to store label data: {ex.Message}");
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
        if (envelope?.Payload == null) return null;

        // 1. Offload CPU-bound Parsing and Mapping
        // This allows the MessageBus listener to go back to the queue immediately.
        return await Task.Run(() =>
        {
            Logger.Debug("[{Workflow}] Generating Broker Request for MQ...", "HandleLabelRequestToMqAsync");

            // 2. Safely parse the JSON
            var payloadString = envelope.Payload.ToString() ?? "{}";
            var node = JsonNode.Parse(payloadString);
            if (node == null) return null;

            var jsonObj = node.AsObject();
            var plc = envelope.Destination.DeviceName;
            var descPoint = jsonObj["DecisionPoint"]?.GetValue<string>();

            // 3. Extract Barcodes safely using LINQ
            var barcodes = jsonObj["Barcodes"]?.AsArray()
                .Select(x => x?.GetValue<string>() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();

            // 4. Construct the outgoing message
            var labelRequest = new LabelRequestFrcMessage(
                Guid.NewGuid(), // Tracking ID
                plc,
                descPoint ?? "Unknown",
                barcodes,
                new Characteristics("0", "0", "0", "0")
            );

            // Return the wrapped envelope
            return new MessageEnvelope(envelope.Destination, labelRequest);
        }, ct);
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
        if (envelope?.Payload == null) return null;

        return await Task.Run(() =>
        {
            var node = JsonNode.Parse(envelope.Payload.ToString() ?? "{}");
            var barcodeArray = node?["Barcodes"]?.AsArray();
            var printerName = envelope.Destination.DeviceName;

            var firstBarcode = (barcodeArray != null && barcodeArray.Count > 0)
                ? barcodeArray[0]?.GetValue<string>()
                : null;

            if (firstBarcode == null) return null;

            var labelData = RetrieveAndDecrement(firstBarcode, printerName);

            if (labelData != null)
            {
                var printerPayload = new
                {
                    Format = "ZPL",
                    Data = labelData.Labels,
                    Meta = new { Barcode = firstBarcode, Type = "Barcode" }
                };

                Logger.Debug("[{Workflow}] Label retrieved for {BC} -> Sending to {Printer}",
                    "HandleBarcodeToPrinterAsync", firstBarcode, printerName);

                return new MessageEnvelope(envelope.Destination, JsonSerializer.Serialize(printerPayload));
            }

            UpdateStatus(WorkflowState.ActiveWithErrors, WorkflowEvent.Error, DeviceHealth.Warning,
                $"Data for Barcode {firstBarcode} not found or printer {printerName} not authorized.");

            return null;
        }, ct);
    }

    /// <summary>
    /// Processes the content payload from a given message envelope and prepares it
    /// to be sent to the specified printer. The method offloads computationally
    /// intensive operations, such as JSON parsing, to a background thread and retrieves
    /// the corresponding label data for the printer. If valid label data is found, it
    /// constructs a new message envelope with the printer payload.
    /// </summary>
    /// <param name="envelope">The message envelope containing the payload to process and the printer destination information.</param>
    /// <param name="ct">The cancellation token used to propagate notifications that the operation should be canceled.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains a message envelope with the printer payload if successfully processed; otherwise, null.</returns>
    private async Task<object?> HandleContentToPrinterAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Payload == null) return null;

        // 1. Offload to the ThreadPool. 
        // This is vital for PDF content which can be several megabytes.
        return await Task.Run(() =>
        {
            // 2. Parse the payload. 
            // In .NET 8, JsonNode is efficient, but parsing large strings is still CPU-bound.
            var node = JsonNode.Parse(envelope.Payload.ToString() ?? "{}");
            var barcodeArray = node?["Barcodes"]?.AsArray();
            var printerName = envelope.Destination.DeviceName;

            var firstBarcode = (barcodeArray != null && barcodeArray.Count > 0)
                ? barcodeArray[0]?.GetValue<string>()
                : null;

            if (firstBarcode == null) return null;

            // 3. Thread-safe retrieval
            // This likely hits your _labelStore (ConcurrentDictionary).
            var labelData = RetrieveAndDecrement(firstBarcode, printerName);

            if (labelData != null)
            {
                var printerPayload = new
                {
                    Format = "PDF",
                    Data = labelData.Labels, // This could be a large Base64 string
                    Meta = new { Barcode = firstBarcode, Type = "Content" }
                };

                Logger.Debug("[{Workflow}] Content label retrieved for {BC} -> Sending to {Printer}",
                    "HandleContentToPrinterAsync", firstBarcode, printerName);

                // 4. Wrap in Envelope and return
                return new MessageEnvelope(envelope.Destination, JsonSerializer.Serialize(printerPayload));
            }

            Logger.Warning("[{Workflow}] Content not found for Barcode: {BC} at Printer: {Printer}",
                "HandleContentToPrinterAsync", firstBarcode, printerName);

            return null;
        }, ct);
    }

    /// <summary>
    /// Retrieves the label data associated with the specified barcode key and decrements the list of pending printers.
    /// If the specified printer is authorized for the barcode and is the last one in the pending list, the barcode data is removed from the store.
    /// </summary>
    /// <param name="key">The unique identifier for the barcode associated with the label data.</param>
    /// <param name="printerName">The name of the printer attempting to process the barcode.</param>
    /// <returns>The label data associated with the barcode if the printer is authorized and processing is successful; otherwise, null.</returns>
    private LabelDataFrcMessage? RetrieveAndDecrement(string key, string printerName)
    {
        if (_labelStore.TryGetValue(key, out var tracker))
        {
            lock (tracker)
            {
                if (tracker.PendingPrinters.Contains(printerName))
                {
                    tracker.PendingPrinters.Remove(printerName);

                    Logger.Debug("[{Workflow}] Printer {Printer} cleared for barcode {BC}. Pending: {Count}",
                        "RetrieveAndDecrement", printerName, key, tracker.PendingPrinters.Count);

                    if (tracker.PendingPrinters.Count == 0)
                    {
                        _labelStore.TryRemove(key, out _);
                        Logger.Verbose("[{Workflow}] Barcode {BC} fully processed and removed from store.",
                            "RetrieveAndDecrement", key);
                    }

                    return tracker.Data;
                }
            }
        }

        Logger.Warning("[{Workflow}] Barcode {BC} not found or printer {Printer} not authorized.",
            "RetrieveAndDecrement", key, printerName);
        return null;
    }

    /// <summary>
    /// Updates the label store with new printer assignments for a given barcode.
    /// If the barcode already exists in the store, its associated printers are updated.
    /// </summary>
    /// <param name="barcode">The unique barcode identifying the label to update.</param>
    /// <param name="printers">A list of printer names to associate with the given barcode.</param>
    /// <param name="ct">A CancellationToken to handle task cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task UpdateLabelStoreAsync(string barcode, List<string> printers, CancellationToken ct)
    {
        _labelStore.AddOrUpdate(barcode,
            new LabelWithTracking(LabelDataFrcMessage.Empty, printers),
            (k, existing) => new LabelWithTracking(existing.Data, printers)
        );
        await Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves the list of next available printers based on the specified decision point.
    /// This method checks printer availability and health asynchronously and ensures the data is up-to-date.
    /// </summary>
    /// <param name="dPoint"></param>
    /// <param name="ct">A CancellationToken to handle cancellation requests for the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of printer identifiers that are available for use.</returns>
    private async Task<List<string>> GetNextAvailablePrintersAsync(string dPoint, CancellationToken ct = default)
    {
        List<string> selectedPrinters = new();

        Logger.Verbose("Entering GetNextAvailablePrintersAsync with Decision Point: {DecisionPoint}.  This should return a list of printers that should print a label to the container.",dPoint);

        // This prevents the Workflow thread from blocking during the LINQ execution
        return await Task.Run(() =>
        {
            using (_lock.EnterScope())
            {
                Logger.FireLogDebug("_printerStatusStore",_printerStatusStore);
                var distinctTypes = _printerStatusStore.Values
                    .Select(p => p.Type)
                    .Distinct()
                    .ToList();

                foreach (var bestForType in distinctTypes.TakeWhile(type => !ct.IsCancellationRequested).Select(type =>
                             _printerStatusStore.Values
                                 .Where(p => p.Type == type && p.Induct == dPoint)
                                 .OrderByDescending(p => p.IsAvailable) // Priority 1: Must be online
                                 .ThenBy(p => p.LastPrinted) // Priority 2: Use the one that didn't go last
                                 .ThenBy(p => p.Name) // Priority 3: Tie-breaker
                                 .FirstOrDefault()).OfType<PrinterStatus>())
                {
                    selectedPrinters.Add(bestForType.Name);

                    bestForType.LastPrinted = !bestForType.LastPrinted;
                }
            }

            return selectedPrinters;
        }, ct);
    }

    /// <summary>
    /// Handles an incoming status message, parses its payload, and updates
    /// the associated printer's status in the printer status store.
    /// Deserializes the payload to extract relevant information and ensures
    /// the printer's status is logged and updated only if a meaningful state change occurs.
    /// </summary>
    /// <param name="envelope">The message envelope containing the topic, payload, and metadata.</param>
    /// <param name="ct">The cancellation token to propagate notification that this operation should be canceled.</param>
    /// <returns>A task representing the asynchronous operation, with an optional result of type object.</returns>
    private async Task HandleStatusMessageAsync(MessageEnvelope? envelope, CancellationToken ct)
    {
        if (envelope?.Destination.DeviceName == null) return;

        var printerName = envelope.Destination.DeviceName;

        try
        {
            // 1. Offload the parsing to the ThreadPool. 
            // This keeps your Message Bus "Inbound" thread free to keep receiving.
            DeviceStatusMessage? statusMsg = await Task.Run(() =>
            {
                if (envelope.Payload is DeviceStatusMessage msg) return msg;

                var json = envelope.Payload?.ToString();
                return string.IsNullOrEmpty(json)
                    ? null
                    : JsonSerializer.Deserialize<DeviceStatusMessage>(json);
            }, ct);

            if (statusMsg == null) return;

            // 2. Logic processing
            if (_printerStatusStore.TryGetValue(printerName, out var status))
            {
                var isNowAvailable = statusMsg.Health == DeviceHealth.Normal;

                // Only log and update if there's an actual state change 
                // This prevents "Log Bloat" in your app logs.
                if (status.IsAvailable != isNowAvailable)
                {
                    status.IsAvailable = isNowAvailable;
                    Logger.Information("[{Workflow}] Printer {Printer} availability changed to: {Available}",
                        "HandleStatusMessageAsync", printerName, isNowAvailable);

                    // 3. Trigger an async update notification if needed
                    // await NotifyAvailabilityChangedAsync(printerName, isNowAvailable);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "[{Workflow}] Failed to parse status message for {Printer}",
                "HandleStatusMessageAsync", printerName);
        }

        // No need for Task.CompletedTask if you actually awaited something above!
    }

    /// <summary>
    /// Converts a generic JSON string into a strongly-typed `LabelRequestFrcMessage` object for the specified Programmable Logic Controller (PLC).
    /// The method extracts necessary metadata such as barcodes, physical characteristics, and the decision point.
    /// It also handles errors gracefully and accounts for possible task cancellation.
    /// </summary>
    /// <param name="plcName">The name of the Programmable Logic Controller (PLC) that the label request is associated with.</param>
    /// <param name="jsonString">The JSON string containing the label request data that needs to be converted.</param>
    /// <param name="ct">An optional cancellation token to cancel the operation if needed.</param>
    /// <returns>A `LabelRequestFrcMessage` object if the conversion is successful; otherwise, null.</returns>
    public async Task<LabelRequestFrcMessage?> ConvertGenericJsonToLabelRequestAsync(string plcName, string jsonString,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Check for cancellation before starting heavy parsing
                ct.ThrowIfCancellationRequested();

                var node = JsonNode.Parse(jsonString);
                if (node == null) return null;

                var jsonObj = node.AsObject();

                // .NET 8 LINQ Optimization: Extract Barcodes
                var barcodes = jsonObj["Barcodes"]?.AsArray()
                    .Select(b => b?.GetValue<string>() ?? string.Empty)
                    .ToList() ?? new List<string>();

                // Map characteristics safely
                var characteristics = new Characteristics(
                    Height: jsonObj["Height"]?.GetValue<string>() ?? "0",
                    Length: jsonObj["Length"]?.GetValue<string>() ?? "0",
                    Width: jsonObj["Width"]?.GetValue<string>() ?? "0",
                    Weight: jsonObj["Weight"]?.GetValue<string>() ?? "0"
                );

                Logger.Debug("[{Workflow}] JSON successfully mapped to LabelRequest for PLC: {PLC}",
                    "ConvertGenericJsonToLabelRequestAsync", plcName);

                // Create the FRC request
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
                return null; // Graceful exit on shutdown
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[{Workflow}] Failed to convert generic JSON for {PLC}",
                    "ConvertGenericJsonToLabelRequestAsync", plcName);
                return null;
            }
        }, ct);
    }
}