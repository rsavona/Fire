using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;

namespace Workflow.PrintAndApplyFrc;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

public record LabelRequestFrcMessage  (
    [property: JsonPropertyName("sessionId")] Guid SessionId, // Updated to Guid
    [property: JsonPropertyName("controllerId")] string ControllerId,
    [property: JsonPropertyName("lineId")] string? LineId,
    [property: JsonPropertyName("barcodes")] List<string> Barcodes,
    [property: JsonPropertyName("characteristics")] Characteristics Characteristics
): DeviceMessageBase
{
    [JsonPropertyName("type")] 
    public string Type { get; init; } = "LabelRequest";
}

public record Characteristics(
    [property: JsonPropertyName("height")] string Height,
    [property: JsonPropertyName("length")] string Length,
    [property: JsonPropertyName("width")] string Width,
    [property: JsonPropertyName("weight")] string Weight
);

public record LabelDataFrcMessage(
    [property: JsonPropertyName("sessionId")] Guid SessionId, // Updated to Guid
    [property: JsonPropertyName("controllerId")] string ControllerId,
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("barcodes")] List<string> Barcodes,
    [property: JsonPropertyName("statusCode")] string StatusCode,
    [property: JsonPropertyName("statusMessage")] string StatusMessage,
    [property: JsonPropertyName("labels")] List<LabelInfo> Labels
): DeviceMessageBase
{
    [JsonPropertyName("type")] 
    public string Type { get; init; } = "LabelData";

    [JsonIgnore]
    public bool IsSuccess => 
        string.Equals(StatusCode, "OKAY", StringComparison.OrdinalIgnoreCase) || 
        StatusCode == "200";

    public string? GetExpectedScan() => 
        Labels?.FirstOrDefault(l => string.Equals(l.ApplicatorType, "SHIPTOP", StringComparison.OrdinalIgnoreCase))?.ExpectedScan;

    public string? GetPrinterDataForType(string applicatorType) =>
        Labels?.FirstOrDefault(label => string.Equals(label.ApplicatorType, applicatorType, StringComparison.OrdinalIgnoreCase))?.PrinterData;

    // Use Guid.Empty for the default state
    public static LabelDataFrcMessage Empty =>
        new(Guid.Empty, string.Empty, string.Empty, new List<string>(), "ERROR", "Empty message", new List<LabelInfo>());
}

public record LabelInfo(
    [property: JsonPropertyName("applicatorType")] string ApplicatorType,
    [property: JsonPropertyName("expectedScan")] string ExpectedScan,
    [property: JsonPropertyName("printerData")] string PrinterData
);

public class LabelVerificationMessage
    {

        public LabelVerificationMessage( Guid sessionId, string controllerId, string lineId, 
            List<string> barcodes, Characteristics characteristics, List<VerificationData> verifications)
        {
            SessionId = sessionId;
            ControllerId = controllerId;
            LineId = lineId;
            Barcodes = barcodes;
            Characteristics = characteristics;
            Verifications = verifications;
        }   
        
        
        // The [JsonPropertyName] attribute maps JSON property names to C# property names.

        [JsonPropertyName("type")]
        public string Type { get; set; } =
            "LabelVerify"; // Message type, constant value "LabelVerify" - must be present

        [JsonPropertyName("sessionId")]
        public Guid SessionId { get; set; } // Universally unique identifier for the labeling session - must be present

        [JsonPropertyName("controllerId")]
        public string ControllerId { get; set; } = string.Empty; // A DC unique controller identifier – must be present

        [JsonPropertyName("lineId")]
        public string LineId { get; set; } =
            string.Empty; // A DC unique Print and Apply line identifier – must be present

        [JsonPropertyName("barcodes")]
        public List<string> Barcodes { get; set; } =
            new List<string>(); // A sequence of barcode values present on the container – must be present

        [JsonPropertyName("characteristics")]
        public Characteristics? Characteristics { get; set; } // A mapping of other container characteristics – optional

        [JsonPropertyName("verifications")]
        public List<VerificationData> Verifications { get; set; } =
            new List<VerificationData>(); // Sequence of mappings describing printing verification results – must be present, can be empty
        public string ToJson()
        {
            return JsonSerializer.Serialize(this);
        }
    }

public class VerificationData
{
    public VerificationData(string applicatorType, string reportedScan, string verifyResult, string? printerName)
    {
        ApplicatorType = applicatorType;
        ReportedScan = reportedScan;
        VerifyResult = verifyResult;
        PrinterName = printerName;
    }

    [JsonPropertyName("applicatorType")]
    public string ApplicatorType { get; set; } =
        string.Empty; // Name of the applicator type (e.g., "SHIP1") - must be present

    [JsonPropertyName("reportedScan")]
    public string ReportedScan { get; set; } =
        string.Empty; // The scanned barcode value that the system read from the printed label - must be present

    [JsonPropertyName("verifyResult")]
    public string VerifyResult { get; set; } =
        string.Empty; // Verification code ("0", "1", "2", etc.) - must be present

    [JsonPropertyName("printerName")]
    public string?
        PrinterName { get; set; } // A DC Unique name for the physical printer that printed the label - optional

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }
   
}

public static class FrcHelper
{
    public static LabelVerificationMessage GetVerificationMessage(LabelDataFrcMessage msg , Characteristics chars)
    {

        return new LabelVerificationMessage(msg.SessionId, msg.ControllerId, msg.LineId, msg.Barcodes,
            chars, msg.Labels.Select(l => new VerificationData(l.ApplicatorType,
                l.ExpectedScan, l.PrinterData, null)).ToList());
    }
}
