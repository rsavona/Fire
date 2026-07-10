using Fusion.Common.BaseClasses;

namespace Fusion.Element.Plc.Suite.Messages;

/// <summary>
/// Typed bus command for decision responses returned to a PLC element.
/// Publish it as the record itself or as JSON (property matching is
/// case-insensitive, so "GIN"/"gin" and "decisionPoint"/"DecisionPoint" all bind).
/// </summary>
public record PlcDecisionCommand : ElementMessageBase
{
    public string? DecisionPoint { get; init; }

    public int? Gin { get; init; }

    /// <summary>Divert/route actions for the decision; preferred field.</summary>
    public List<string>? Actions { get; init; }

    /// <summary>Legacy alias for Actions used by some reactions.</summary>
    public List<string>? DecisionPoints { get; init; }
}
