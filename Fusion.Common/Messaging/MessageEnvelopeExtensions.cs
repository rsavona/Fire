using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fusion.Common;

/// <summary>
/// Normalizes the three personalities of MessageEnvelope.Payload (typed object,
/// JSON string, byte[]) into a single parsing surface, so bus consumers no longer
/// need per-manager JsonNode probing.
/// </summary>
public static class MessageEnvelopeExtensions
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Attempts to interpret the envelope payload as a <typeparamref name="T"/> command.
    /// Accepts an already-typed payload, a JSON string (property matching is
    /// case-insensitive; numbers may arrive as strings), or UTF-8 bytes.
    /// Never throws: on failure, <paramref name="error"/> describes why.
    /// </summary>
    public static bool TryGetPayload<T>(this MessageEnvelope envelope, out T? command, out string? error)
        where T : class
    {
        command = null;
        error = null;

        switch (envelope.Payload)
        {
            case null:
                error = "Payload is null.";
                return false;
            case T typed:
                command = typed;
                return true;
        }

        string json = envelope.GetPayloadText();
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Payload is empty.";
            return false;
        }

        try
        {
            command = JsonSerializer.Deserialize<T>(json, ParseOptions);
            if (command == null)
            {
                error = $"Payload deserialized to null {typeof(T).Name}.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Payload is not valid {typeof(T).Name} JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns the payload as text regardless of how it was published:
    /// strings pass through, byte[] is decoded as UTF-8, and any other
    /// object is JSON-serialized.
    /// </summary>
    public static string GetPayloadText(this MessageEnvelope envelope)
    {
        return envelope.Payload switch
        {
            null => string.Empty,
            string s => s,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => SerializeUnknown(envelope.Payload)
        };
    }

    /// <summary>
    /// Builds a header for an envelope DERIVED from this one (a reaction result,
    /// a query response, a forwarded copy): the CorrelationId is inherited so the
    /// whole chain shares one trace identity, and the parent's MessageId is
    /// recorded as CausationId. Only genuinely new work (fresh input arriving
    /// from the wire) should mint a new CorrelationId.
    /// </summary>
    public static MessageHeader DeriveHeader(this MessageEnvelope parent, string? source = null)
    {
        var header = new MessageHeader
        {
            Source = source ?? parent.Header.Source,
            CorrelationId = parent.Header.CorrelationId
        };
        header.Metadata["CausationId"] = parent.Header.MessageId.ToString();
        return header;
    }

    /// <summary>
    /// Stamps an existing envelope with the parent's correlation identity.
    /// Use when a handler has already constructed the result envelope.
    /// </summary>
    public static MessageEnvelope WithCorrelationFrom(this MessageEnvelope envelope, MessageEnvelope parent)
    {
        return envelope with
        {
            Header = envelope.Header with { CorrelationId = parent.Header.CorrelationId }
        };
    }

    private static string SerializeUnknown(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, payload.GetType());
        }
        catch (Exception)
        {
            return payload.ToString() ?? string.Empty;
        }
    }
}
