using System.Text.Json;
using Fusion.Common.BaseClasses;

namespace Fusion.Element.Printer.Suite;

/// <summary>
/// Typed bus command for print jobs. Raw label data (ZPL starting with '^' or
/// XML starting with '&lt;') bypasses this and is printed directly; anything else
/// is parsed into this shape (property matching is case-insensitive, so
/// "printerData"/"PrinterData" both bind).
/// </summary>
public record PrintJobCommand : ElementMessageBase
{
    /// <summary>Label data at the job root; may be a JSON string or an array of strings.</summary>
    public JsonElement? PrinterData { get; init; }

    public List<PrintJobLabel>? Labels { get; init; }
}

public record PrintJobLabel
{
    /// <summary>Label data; may be a JSON string or an array of strings.</summary>
    public JsonElement? PrinterData { get; init; }

    /// <summary>Matched against the element's configured PrintType (e.g. SHIPTOP).</summary>
    public string? ApplicatorType { get; init; }
}
