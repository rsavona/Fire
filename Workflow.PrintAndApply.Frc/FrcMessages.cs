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
