using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fusion.Element.Plc.Suite.Virtual;

internal sealed class ScriptedPlcScenario
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "scripted-plc-scenario";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("virtualPlcProperties")]
    public ScriptedVirtualPlcProperties? VirtualPlcProperties { get; set; }

    [JsonPropertyName("stages")]
    public List<ScriptedPlcStage> Stages { get; set; } = [];

    public static ScriptedPlcScenario Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScriptedPlcScenario>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to deserialize scripted PLC scenario from {path}");
    }
}

internal sealed class ScriptedVirtualPlcProperties
{
    [JsonPropertyName("DecisionPoints")]
    public string? DecisionPoints { get; set; }

    [JsonPropertyName("Printer1")]
    public string? Printer1 { get; set; }

    [JsonPropertyName("Printer2")]
    public string? Printer2 { get; set; }

    [JsonPropertyName("TotalTotes")]
    public int? TotalTotes { get; set; }

    [JsonPropertyName("InductionFreq")]
    public int? InductionFreq { get; set; }

    [JsonPropertyName("BarcodeList")]
    public List<string> BarcodeList { get; set; } = [];
}

internal sealed class ScriptedPlcStage
{
    [JsonPropertyName("stage")]
    public int Stage { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("barcodeCount")]
    public int? BarcodeCount { get; set; }

    [JsonPropertyName("inductionSpacingMs")]
    public int InductionSpacingMs { get; set; }

    [JsonPropertyName("expectedFlow")]
    public string? ExpectedFlow { get; set; }

    [JsonPropertyName("cycleLength")]
    public int? CycleLength { get; set; }

    [JsonPropertyName("inductMetadata")]
    public JsonObject? InductMetadata { get; set; }

    [JsonPropertyName("barcodes")]
    public List<ScriptedBarcodeEntry> Barcodes { get; set; } = [];

    [JsonPropertyName("cycles")]
    public List<ScriptedPrinterCycle> Cycles { get; set; } = [];

    [JsonPropertyName("barcodePattern")]
    public ScriptedBarcodePattern? BarcodePattern { get; set; }

    public IEnumerable<ScriptedPlcTote> ExpandTotes()
    {
        if (Barcodes.Count > 0)
        {
            foreach (var entry in Barcodes)
            {
                yield return new ScriptedPlcTote(
                    Stage,
                    Name,
                    entry.Gin,
                    entry.Barcode,
                    entry.InductAtMs,
                    entry.InductMetadata ?? InductMetadata,
                    entry.SequenceAnomaly,
                    entry.ExpectedOutcome);
            }

            yield break;
        }

        if (BarcodePattern == null)
        {
            yield break;
        }

        int index = 0;
        for (int gin = BarcodePattern.GinStart; gin <= BarcodePattern.GinEnd; gin++)
        {
            index++;
            var metadata = GetCycleMetadata(index) ?? InductMetadata;
            var barcode = $"{BarcodePattern.BarcodePrefix}{index:D4}";

            yield return new ScriptedPlcTote(
                Stage,
                Name,
                gin,
                barcode,
                (index - 1) * InductionSpacingMs,
                metadata,
                null,
                BarcodePattern.ExpectedOutcome);
        }
    }

    private JsonObject? GetCycleMetadata(int oneBasedPosition)
    {
        if (Cycles.Count == 0 || CycleLength is null or <= 0)
        {
            return null;
        }

        int cycleIndex = (oneBasedPosition - 1) / CycleLength.Value;
        return cycleIndex >= 0 && cycleIndex < Cycles.Count
            ? Cycles[cycleIndex].InductMetadata
            : null;
    }
}

internal sealed class ScriptedBarcodeEntry
{
    [JsonPropertyName("gin")]
    public int Gin { get; set; }

    [JsonPropertyName("barcode")]
    public string Barcode { get; set; } = string.Empty;

    [JsonPropertyName("inductAtMs")]
    public int InductAtMs { get; set; }

    [JsonPropertyName("inductMetadata")]
    public JsonObject? InductMetadata { get; set; }

    [JsonPropertyName("sequenceAnomaly")]
    public string? SequenceAnomaly { get; set; }

    [JsonPropertyName("expectedOutcome")]
    public string? ExpectedOutcome { get; set; }
}

internal sealed class ScriptedPrinterCycle
{
    [JsonPropertyName("inductMetadata")]
    public JsonObject? InductMetadata { get; set; }
}

internal sealed class ScriptedBarcodePattern
{
    [JsonPropertyName("ginStart")]
    public int GinStart { get; set; }

    [JsonPropertyName("ginEnd")]
    public int GinEnd { get; set; }

    [JsonPropertyName("barcodePrefix")]
    public string BarcodePrefix { get; set; } = string.Empty;

    [JsonPropertyName("expectedOutcome")]
    public string? ExpectedOutcome { get; set; }
}

internal sealed record ScriptedPlcTote(
    int Stage,
    string StageName,
    int Gin,
    string Barcode,
    int InductAtMs,
    JsonObject? InductMetadata,
    string? SequenceAnomaly,
    string? ExpectedOutcome);
