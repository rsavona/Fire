using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workflow.PrintAndApplyFrc;

public record LabelRequestFrcMessage(
    [property: JsonPropertyName("sessionId")]
    Guid SessionId,
    [property: JsonPropertyName("controllerId")]
    string ControllerId,
    [property: JsonPropertyName("lineId")] string? LineId,
    [property: JsonPropertyName("barcodes")]
    List<string> Barcodes,
    [property: JsonPropertyName("characteristics")]
    Characteristics Characteristics
)
{
    [JsonPropertyName("type")] public string Type { get; } = "LabelRequest";
}

public record Characteristics(
    [property: JsonPropertyName("height")] string Height,
    [property: JsonPropertyName("length")] string Length,
    [property: JsonPropertyName("width")] string Width,
    [property: JsonPropertyName("weight")] string Weight
);

// Refactoring LabelDataMessageFrc
public record LabelDataFrcMessage(
    [property: JsonPropertyName("sessionId")]
    string SessionId,
    [property: JsonPropertyName("controllerId")]
    string ControllerId,
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("barcodes")]
    List<string> Barcodes,
    [property: JsonPropertyName("statusCode")]
    string StatusCode,
    [property: JsonPropertyName("statusMessage")]
    string StatusMessage,
    [property: JsonPropertyName("labels")] List<LabelInfo> Labels
)
{
    [JsonPropertyName("type")] public string Type { get; init; } = "LabelData"; // Default

    // Logic belongs in methods, not constructors
    public string? GetExpectedScan() => Labels?.FirstOrDefault(l => l.ApplicatorType == "SHIPTOP")?.ExpectedScan;

    public string? GetPrinterDataForType(string applicatorType) =>
        Labels?.FirstOrDefault(label => label.ApplicatorType == applicatorType)?.PrinterData;

    public static LabelDataFrcMessage Empty =>
        new(string.Empty, string.Empty, string.Empty, new List<string>(),
            string.Empty, string.Empty, new List<LabelInfo>());
}

public record LabelInfo(string applicatorType, string expectedScan, string printerData)
{
    [JsonPropertyName("applicatorType")] public string ApplicatorType { get; set; } = applicatorType;

    [JsonPropertyName("expectedScan")] public string ExpectedScan { get; set; } = expectedScan;

    [JsonPropertyName("printerData")] public string PrinterData { get; set; } = printerData;
}

