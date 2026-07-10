using System.Text.Json;
using System.Text.Json.Serialization;
using Fusion.Common;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;

namespace Fusion.Element.Link;

public enum LinkFrameKind
{
    Subscribe,
    Unsubscribe,
    Publish,
    Heartbeat,
    HeartbeatAck
}

/// <summary>
/// A single newline-terminated JSON frame exchanged between a LinkClientElement
/// and a LinkServerElement. Compact JSON never contains a raw newline, so '\n'
/// is a safe frame terminator even for payloads with embedded (escaped) newlines.
/// </summary>
public record LinkFrame
{
    public LinkFrameKind Kind { get; init; }

    /// <summary>Identity of the Fusion instance that produced this frame.</summary>
    public string Origin { get; init; } = string.Empty;

    /// <summary>Topic patterns for Subscribe/Unsubscribe frames.</summary>
    public List<string>? Topics { get; init; }

    /// <summary>Concrete destination topic for Publish frames.</summary>
    public string? Topic { get; init; }

    public string? Payload { get; init; }
    public int Gin { get; init; }
    public bool HighPriority { get; init; } = true;

    /// <summary>Original Header.Source from the remote envelope.</summary>
    public string? SourceElement { get; init; }

    public Guid CorrelationId { get; init; }

    public const char Terminator = '\n';

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToWire() => JsonSerializer.Serialize(this, WireOptions) + Terminator;

    public static LinkFrame? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<LinkFrame>(raw, WireOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Shared conventions for the Fusion Link protocol: loop-guard tagging,
/// instance identity, and envelope/frame translation.
/// </summary>
public static class LinkConventions
{
    /// <summary>
    /// Envelopes republished from a remote instance carry a Client value with this
    /// prefix. Envelopes tagged this way are never forwarded back over a link,
    /// which prevents echo loops when two instances are linked to each other.
    /// </summary>
    public const string ClientPrefix = "LINK:";

    public static bool IsFromLink(MessageEnvelope envelope) =>
        envelope.Client?.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Resolves the identity this instance announces over the wire:
    /// an explicit "Origin" element property, else the blueprint service/customer
    /// name, else the machine name.
    /// </summary>
    public static string ResolveOrigin(IElementBlueprint config)
    {
        if (config.Properties.TryGetValue("Origin", out var origin) &&
            !string.IsNullOrWhiteSpace(origin?.ToString()))
        {
            return origin.ToString()!;
        }

        var space = ConfigurationLoader.GetSpaceConfig();
        if (!string.IsNullOrWhiteSpace(space?.ServiceName)) return space.ServiceName!;
        if (!string.IsNullOrWhiteSpace(space?.CustomerName)) return space.CustomerName;

        return Environment.MachineName;
    }

    public static LinkFrame ToPublishFrame(MessageEnvelope envelope, string origin) => new()
    {
        Kind = LinkFrameKind.Publish,
        Origin = origin,
        Topic = envelope.Destination.ToString(),
        Payload = SerializePayload(envelope.Payload),
        Gin = envelope.Gin,
        HighPriority = envelope.IsHighPriority,
        SourceElement = envelope.Header.Source,
        CorrelationId = envelope.Header.CorrelationId
    };

    public static MessageEnvelope ToLocalEnvelope(LinkFrame frame)
    {
        string linkClient = $"{ClientPrefix}{frame.Origin}";
        var header = new MessageHeader
        {
            Source = string.IsNullOrEmpty(frame.SourceElement) ? linkClient : frame.SourceElement,
            CorrelationId = frame.CorrelationId == default ? Guid.NewGuid() : frame.CorrelationId
        };

        return new MessageEnvelope(
            frame.Topic!, frame.Payload ?? string.Empty, frame.Gin, linkClient, frame.HighPriority, header);
    }

    public static string SerializePayload(object? payload) => payload switch
    {
        null => string.Empty,
        string s => s,
        _ => JsonSerializer.Serialize(payload)
    };

    public static IReadOnlyList<string> ParseTopicList(IElementBlueprint config, string key)
    {
        if (!config.Properties.TryGetValue(key, out var raw))
        {
            return [];
        }

        return (raw?.ToString() ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
