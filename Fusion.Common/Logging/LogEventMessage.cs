using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog.Events;
using Serilog.Parsing;

namespace Fusion.Common.Logging;

/// <summary>
/// A fully-structured, transport-friendly log event. This is the payload format used by
/// the two-tier logging pipeline:
///   Tier 1 (raw firehose): every event flows over the dedicated LoggingBus wrapped in a LogMessage.
///   Tier 2 (curated): the Logger element republishes filtered events on the main MessageBus
///   as LogEventMessage payloads on topics "SYS.LOG.{ElementName}.{Level}".
/// Serializes to/from CLEF (Serilog Compact Log Event Format: @t, @l, @mt, @m, @x + properties).
/// </summary>
public record LogEventMessage
{
    /// <summary>Event timestamp, always UTC.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public LogEventLevel Level { get; init; } = LogEventLevel.Information;

    /// <summary>The Serilog message template (with named holes), not a flattened string.</summary>
    public string MessageTemplate { get; init; } = string.Empty;

    /// <summary>The template rendered with its properties.</summary>
    public string RenderedMessage { get; init; } = string.Empty;

    /// <summary>Named structured properties bound from the template arguments.</summary>
    public Dictionary<string, object?> Properties { get; init; } = new();

    /// <summary>Exception details (ToString), if any. A string so the event stays serializable.</summary>
    public string? Exception { get; init; }

    /// <summary>Source context / element name that produced the event.</summary>
    public string ElementName { get; init; } = "System";

    private static readonly MessageTemplateParser TemplateParser = new();

    /// <summary>
    /// Topic used when this event is republished on the main MessageBus.
    /// </summary>
    public string GetRepublishTopic() => $"SYS.LOG.{ElementName}.{Level}";

    // ---------------------------------------------------------------------
    //                        CLEF SERIALIZATION
    // ---------------------------------------------------------------------

    /// <summary>
    /// Serializes this event as a single-line CLEF (compact log event format) JSON document.
    /// Reserved fields: @t (timestamp), @l (level), @mt (template), @m (rendered), @x (exception).
    /// User properties whose names start with '@' are escaped with a second '@'.
    /// </summary>
    public string ToClef()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            var utc = Timestamp.Kind == DateTimeKind.Utc ? Timestamp : Timestamp.ToUniversalTime();
            writer.WriteString("@t", utc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("@l", Level.ToString());
            writer.WriteString("@mt", MessageTemplate);
            writer.WriteString("@m", RenderedMessage);
            if (Exception != null) writer.WriteString("@x", Exception);

            writer.WriteString("ElementName", ElementName);

            foreach (var kv in Properties)
            {
                if (string.IsNullOrEmpty(kv.Key) ||
                    string.Equals(kv.Key, "ElementName", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = kv.Key.StartsWith('@') ? "@" + kv.Key : kv.Key;
                WriteClefValue(writer, name, kv.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteClefValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(name); break;
            case string s: writer.WriteString(name, s); break;
            case bool b: writer.WriteBoolean(name, b); break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong ul: writer.WriteNumber(name, ul); break;
            case float f: writer.WriteNumber(name, f); break;
            case double d: writer.WriteNumber(name, d); break;
            case decimal m: writer.WriteNumber(name, m); break;
            case DateTime dt: writer.WriteString(name, dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: writer.WriteString(name, dto.ToString("O", CultureInfo.InvariantCulture)); break;
            default:
                try
                {
                    writer.WritePropertyName(name);
                    JsonSerializer.Serialize(writer, value, value.GetType());
                }
                catch (Exception)
                {
                    // Fall back to the string representation; never let serialization take down logging.
                    writer.WriteStringValue(value.ToString() ?? string.Empty);
                }
                break;
        }
    }

    /// <summary>
    /// Parses a single CLEF JSON document into a LogEventMessage.
    /// Missing @l defaults to Information (per the CLEF specification).
    /// </summary>
    public static LogEventMessage FromClef(string clefJson)
    {
        using var doc = JsonDocument.Parse(clefJson);
        var root = doc.RootElement;

        DateTime timestamp = DateTime.UtcNow;
        var level = LogEventLevel.Information;
        string messageTemplate = string.Empty;
        string renderedMessage = string.Empty;
        string? exception = null;
        string elementName = "System";
        var properties = new Dictionary<string, object?>();

        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "@t":
                    if (DateTime.TryParse(prop.Value.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
                    {
                        timestamp = ts;
                    }
                    break;
                case "@l":
                    Enum.TryParse(prop.Value.GetString(), true, out level);
                    break;
                case "@mt": messageTemplate = prop.Value.GetString() ?? string.Empty; break;
                case "@m": renderedMessage = prop.Value.GetString() ?? string.Empty; break;
                case "@x": exception = prop.Value.GetString(); break;
                case "ElementName": elementName = prop.Value.GetString() ?? "System"; break;
                default:
                    if (prop.Name.StartsWith('@') && !prop.Name.StartsWith("@@")) break; // unknown reified field
                    string name = prop.Name.StartsWith("@@") ? prop.Name[1..] : prop.Name;
                    properties[name] = ReadClefValue(prop.Value);
                    break;
            }
        }

        return new LogEventMessage
        {
            Timestamp = timestamp,
            Level = level,
            MessageTemplate = messageTemplate,
            RenderedMessage = renderedMessage,
            Exception = exception,
            ElementName = elementName,
            Properties = properties
        };
    }

    private static object? ReadClefValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : (object)element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    // ---------------------------------------------------------------------
    //                        CONVERSION HELPERS
    // ---------------------------------------------------------------------

    /// <summary>
    /// Converts a raw LoggingBus LogMessage into a structured LogEventMessage by binding
    /// the positional args to the named holes in the message template.
    /// Used when a LoggingBus publisher did not attach a pre-built event.
    /// </summary>
    public static LogEventMessage FromLogMessage(LogMessage message)
    {
        var (rendered, properties) = BindTemplate(message.MessageTemplate, message.Args);

        if (string.IsNullOrEmpty(rendered))
        {
            rendered = string.IsNullOrEmpty(message.FormattedMessage)
                ? message.MessageTemplate
                : message.FormattedMessage;
        }

        return new LogEventMessage
        {
            Timestamp = message.Timestamp.Kind == DateTimeKind.Utc
                ? message.Timestamp
                : message.Timestamp.ToUniversalTime(),
            Level = message.Level,
            MessageTemplate = message.MessageTemplate,
            RenderedMessage = rendered,
            Exception = message.Exception?.ToString(),
            ElementName = string.IsNullOrEmpty(message.Context) ? "System" : message.Context,
            Properties = properties
        };
    }

    /// <summary>
    /// Binds positional args to the named holes of a Serilog message template.
    /// Returns the rendered message and the named property dictionary (scalars unwrapped).
    /// Never throws.
    /// </summary>
    public static (string Rendered, Dictionary<string, object?> Properties) BindTemplate(
        string messageTemplate, object?[]? args)
    {
        var properties = new Dictionary<string, object?>();
        try
        {
            var template = TemplateParser.Parse(messageTemplate ?? string.Empty);
            var tokens = template.Tokens.OfType<PropertyToken>().ToList();
            var boundValues = new Dictionary<string, LogEventPropertyValue>();

            if (args is { Length: > 0 } && tokens.Count > 0)
            {
                for (int i = 0; i < tokens.Count && i < args.Length; i++)
                {
                    var value = args[i];
                    boundValues[tokens[i].PropertyName] = new ScalarValue(value);
                    properties[tokens[i].PropertyName] = value is null or string or bool or byte or sbyte
                        or short or ushort or int or uint or long or ulong or float or double or decimal
                        or DateTime or DateTimeOffset
                        ? value
                        : value.ToString();
                }
            }

            using var sw = new StringWriter(CultureInfo.InvariantCulture);
            template.Render(boundValues, sw);
            return (sw.ToString(), properties);
        }
        catch (Exception)
        {
            return (messageTemplate ?? string.Empty, properties);
        }
    }
}
