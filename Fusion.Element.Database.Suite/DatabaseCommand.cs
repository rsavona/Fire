using System.Text.Json;
using Fusion.Common.BaseClasses;

namespace Fusion.Element.Database.Suite;

/// <summary>
/// Typed bus command for database elements. Publish it as the record itself or as
/// JSON (property matching is case-insensitive, so "GIN"/"gin" and
/// "decisionPoint"/"DecisionPoint" all bind).
/// </summary>
public record DatabaseCommand : ElementMessageBase
{
    public string? Sql { get; init; }

    /// <summary>"QUERY" publishes results back to ELEMENT.QueryResult.*; anything else executes.</summary>
    public string? Operation { get; init; }

    /// <summary>System.Data.CommandType name; defaults to Text.</summary>
    public string? CommandType { get; init; }

    public Dictionary<string, JsonElement>? Parameters { get; init; }

    /// <summary>Optional correlation fields echoed back on query responses.</summary>
    public string? DecisionPoint { get; init; }

    public int? Gin { get; init; }
}
