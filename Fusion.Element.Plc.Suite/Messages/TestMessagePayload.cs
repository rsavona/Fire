using System.Text.Json.Serialization;

namespace Fusion.Element.Plc.Suite.Messages;

public record TestMessagePayload(
    [property: JsonPropertyName("TestName")]
    string TestName,
    [property: JsonPropertyName("Description")]
    string? Description,
    [property: JsonPropertyName("ScriptPath")]
    string? ScriptPath,
    [property: JsonPropertyName("GeneratedAtUtc")]
    string GeneratedAtUtc,
    [property: JsonPropertyName("StartsAtUtc")]
    string StartsAtUtc,
    [property: JsonPropertyName("StartsInSeconds")]
    int StartsInSeconds,
    [property: JsonPropertyName("StageCount")]
    int StageCount,
    [property: JsonPropertyName("ToteCount")]
    int ToteCount,
    [property: JsonPropertyName("DecisionChain")]
    string? DecisionChain,
    [property: JsonPropertyName("Printer1")]
    string? Printer1,
    [property: JsonPropertyName("Printer2")]
    string? Printer2,
    [property: JsonPropertyName("Stages")]
    List<TestStageSummary> Stages
) : PlcPayloadBase
{
    [JsonIgnore] public override PlcMessageHeaders Header => PlcMessageHeaders.TEST;

    [JsonPropertyName("Event")]
    public string Event { get; init; } = "START";

    [JsonPropertyName("Status")]
    public string Status { get; init; } = "PENDING";

    [JsonPropertyName("CompletedAtUtc")]
    public string? CompletedAtUtc { get; init; }

    [JsonPropertyName("DurationSeconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("CompletedStageCount")]
    public int? CompletedStageCount { get; init; }

    [JsonPropertyName("CompletedToteCount")]
    public int? CompletedToteCount { get; init; }
}

public record TestStageSummary(
    [property: JsonPropertyName("Stage")]
    int Stage,
    [property: JsonPropertyName("Name")]
    string Name,
    [property: JsonPropertyName("ToteCount")]
    int ToteCount,
    [property: JsonPropertyName("FirstGin")]
    int? FirstGin,
    [property: JsonPropertyName("LastGin")]
    int? LastGin,
    [property: JsonPropertyName("InductionSpacingMs")]
    int InductionSpacingMs,
    [property: JsonPropertyName("ExpectedOutcomes")]
    List<string> ExpectedOutcomes,
    [property: JsonPropertyName("ExpectedFlow")]
    string? ExpectedFlow
);
