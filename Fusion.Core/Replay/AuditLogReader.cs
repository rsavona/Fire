using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;

namespace Fusion.Core.Replay;

/// <summary>
/// A single message recovered from a BusAuditLogger CLEF audit file.
/// </summary>
public sealed record AuditRecord
{
    public DateTimeOffset Timestamp { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string PayloadType { get; init; } = string.Empty;

    /// <summary>The rendered (ToString) payload text. Always present.</summary>
    public string PayloadText { get; init; } = string.Empty;

    /// <summary>
    /// The structured payload object, when the audit log was written with
    /// BusAuditLogger.CapturePayloads (or the payload is a FlowEvent). Null for legacy lines.
    /// </summary>
    public JsonElement? PayloadJson { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>
/// Describes one audit file on disk as a replayable time window.
/// </summary>
public sealed record AuditWindow
{
    public string FilePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? Last { get; init; }
    public int RecordCount { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>
/// Lightweight element status recovered from a replayed audit record. Real
/// ElementStatusMessage instances cannot be reconstructed from their ToString()
/// rendering, so replay consumers get this reduced view (id + state + health).
/// </summary>
public sealed record ReplayedElementStatus(string ElementId, string State, ElementHealth Health);

/// <summary>
/// Parses the CLEF (compact JSON) audit files written by <see cref="BusAuditLogger"/> via Serilog
/// (Output/logs/audit/{config}_audit_{yyyyMMdd}.json).
/// Supports both formats:
///  - Structured (current): Topic / PayloadType / Payload are first-class CLEF properties.
///  - Legacy: everything embedded in the rendered "@mt" string "[topic] [type]  text".
/// </summary>
public static class AuditLogReader
{
    // Legacy @mt format: $"[{topic,-40}] [{messageType,-15}]  {message}"
    private static readonly Regex LegacyTemplateRegex = new(
        @"^\[(?<topic>\S+)\s*\] \[(?<type>\S+)\s*\]\s{1,2}(?<payload>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ElementStatusMessage record ToString rendering, e.g.
    // "ElementStatusMessage { ElementId = FUSION-SYS-HOST_A, ... State = Running, Health = Normal, ... }"
    private static readonly Regex StatusTextRegex = new(
        @"ElementId\s*=\s*(?<id>[^,}]+).*?State\s*=\s*(?<state>[^,}]+),\s*Health\s*=\s*(?<health>\w+)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static bool TryParseLine(string? line, out AuditRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("@t", out var tProp)) return false;
            if (!DateTimeOffset.TryParse(tProp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp)) return false;

            string? correlationId = root.TryGetProperty("CorrelationId", out var corr)
                ? corr.GetString()
                : null;

            // Structured format: Topic / PayloadType properties present.
            if (root.TryGetProperty("Topic", out var topicProp) &&
                root.TryGetProperty("PayloadType", out var typeProp))
            {
                JsonElement? payloadJson = null;
                string payloadText = string.Empty;

                if (root.TryGetProperty("Payload", out var payloadProp))
                {
                    if (payloadProp.ValueKind == JsonValueKind.String)
                    {
                        payloadText = payloadProp.GetString() ?? string.Empty;
                    }
                    else
                    {
                        payloadJson = payloadProp.Clone();
                        payloadText = payloadProp.GetRawText();
                    }
                }

                record = new AuditRecord
                {
                    Timestamp = timestamp,
                    Topic = topicProp.GetString() ?? string.Empty,
                    PayloadType = typeProp.GetString() ?? string.Empty,
                    PayloadText = payloadText,
                    PayloadJson = payloadJson,
                    CorrelationId = correlationId
                };
                return record.Topic.Length > 0;
            }

            // Legacy format: parse the rendered message template.
            if (root.TryGetProperty("@mt", out var mtProp))
            {
                var mt = mtProp.GetString();
                if (mt == null) return false;

                var match = LegacyTemplateRegex.Match(mt);
                if (!match.Success) return false;

                record = new AuditRecord
                {
                    Timestamp = timestamp,
                    Topic = match.Groups["topic"].Value,
                    PayloadType = match.Groups["type"].Value,
                    PayloadText = match.Groups["payload"].Value,
                    CorrelationId = correlationId
                };
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Streams all parseable records from an audit file, in file order.</summary>
    public static IEnumerable<AuditRecord> ReadRecords(string filePath)
    {
        // Share-friendly open: the file may still be written to by a live system.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (TryParseLine(line, out var record))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Filters records to a time window. Null bounds are open-ended.
    /// </summary>
    public static IEnumerable<AuditRecord> FilterWindow(
        IEnumerable<AuditRecord> records, DateTimeOffset? from, DateTimeOffset? to)
    {
        foreach (var record in records)
        {
            if (from.HasValue && record.Timestamp < from.Value) continue;
            if (to.HasValue && record.Timestamp > to.Value) continue;
            yield return record;
        }
    }

    /// <summary>
    /// Lists the audit files in a directory as replayable windows (newest first).
    /// Scans each file once to determine its first/last record timestamps.
    /// </summary>
    public static IReadOnlyList<AuditWindow> ListWindows(string auditDirectory)
    {
        var windows = new List<AuditWindow>();
        if (!Directory.Exists(auditDirectory)) return windows;

        foreach (var file in Directory.EnumerateFiles(auditDirectory, "*audit*.json"))
        {
            DateTimeOffset? first = null, last = null;
            int count = 0;
            try
            {
                foreach (var record in ReadRecords(file))
                {
                    first ??= record.Timestamp;
                    last = record.Timestamp;
                    count++;
                }
            }
            catch (IOException)
            {
                continue; // Skip files we cannot read (still locked exclusively, etc.)
            }

            windows.Add(new AuditWindow
            {
                FilePath = file,
                Name = Path.GetFileNameWithoutExtension(file),
                First = first,
                Last = last,
                RecordCount = count,
                SizeBytes = new FileInfo(file).Length
            });
        }

        return windows.OrderByDescending(w => w.Last ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>
    /// Attempts to reconstruct a FlowEvent from a record (requires structured payload capture).
    /// </summary>
    public static bool TryParseFlowEvent(AuditRecord record, out FlowEvent flowEvent)
    {
        flowEvent = null!;
        if (!string.Equals(record.PayloadType, nameof(FlowEvent), StringComparison.OrdinalIgnoreCase))
            return false;
        if (record.PayloadJson is not { ValueKind: JsonValueKind.Object } json) return false;

        string GetString(string name) =>
            json.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? string.Empty
                : string.Empty;

        flowEvent = new FlowEvent
        {
            Source = GetString(nameof(FlowEvent.Source)),
            Force = GetString(nameof(FlowEvent.Force)),
            Destination = GetString(nameof(FlowEvent.Destination)),
            Timestamp = record.Timestamp.UtcDateTime
        };
        return flowEvent.Source.Length > 0 || flowEvent.Destination.Length > 0;
    }

    /// <summary>
    /// Attempts to recover a reduced element status (id/state/health) from a record.
    /// Works for both structured payloads and legacy ToString() renderings.
    /// </summary>
    public static bool TryParseElementStatus(AuditRecord record, out ReplayedElementStatus status)
    {
        status = null!;
        if (!record.PayloadType.Contains("Status", StringComparison.OrdinalIgnoreCase)) return false;

        // Structured payload first.
        if (record.PayloadJson is { ValueKind: JsonValueKind.Object } json)
        {
            string? id = json.TryGetProperty("ElementId", out var idProp)
                ? idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.GetRawText()
                : null;
            string state = json.TryGetProperty("State", out var stateProp) ? stateProp.GetString() ?? "" : "";
            string healthText = json.TryGetProperty("Health", out var healthProp) ? healthProp.ToString() : "";

            if (!string.IsNullOrEmpty(id))
            {
                status = new ReplayedElementStatus(id.Trim(), state.Trim(), ParseHealth(healthText));
                return true;
            }
        }

        // Legacy rendered text.
        var match = StatusTextRegex.Match(record.PayloadText);
        if (!match.Success) return false;

        status = new ReplayedElementStatus(
            match.Groups["id"].Value.Trim(),
            match.Groups["state"].Value.Trim(),
            ParseHealth(match.Groups["health"].Value));
        return true;
    }

    private static ElementHealth ParseHealth(string text)
    {
        return Enum.TryParse<ElementHealth>(text, ignoreCase: true, out var health)
            ? health
            : ElementHealth.Normal;
    }
}
