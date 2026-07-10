using System.Globalization;

namespace Fusion.Element.Sparkplug.Protocol;

/// <summary>
/// A single Sparkplug B metric: name, optional alias, data type, timestamp,
/// and a typed value. Immutable by convention — build a new instance to change
/// a value (the session layer compares instances for report-by-exception).
/// </summary>
public sealed record SparkplugMetric
{
    public string Name { get; init; } = string.Empty;
    public ulong? Alias { get; init; }
    public SparkplugDataType DataType { get; init; } = SparkplugDataType.Unknown;
    public ulong TimestampMs { get; init; }
    public object? Value { get; init; }
    public bool IsNull => Value is null;

    public static ulong NowMs() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Creates a metric, inferring the Sparkplug data type from the CLR value type.</summary>
    public static SparkplugMetric Create(string name, object? value, ulong? timestampMs = null)
    {
        var (dataType, normalized) = Normalize(value);
        return new SparkplugMetric
        {
            Name = name,
            DataType = dataType,
            Value = normalized,
            TimestampMs = timestampMs ?? NowMs()
        };
    }

    /// <summary>
    /// Creates a metric from free text: numeric text becomes a Double metric,
    /// boolean text becomes Boolean, anything else stays String.
    /// </summary>
    public static SparkplugMetric FromText(string name, string text, ulong? timestampMs = null)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return Create(name, d, timestampMs);
        if (bool.TryParse(text, out bool b))
            return Create(name, b, timestampMs);
        return Create(name, text, timestampMs);
    }

    private static (SparkplugDataType, object?) Normalize(object? value) => value switch
    {
        null => (SparkplugDataType.String, null),
        bool b => (SparkplugDataType.Boolean, b),
        sbyte or short or int => (SparkplugDataType.Int64, Convert.ToInt64(value)),
        long l => (SparkplugDataType.Int64, l),
        byte or ushort or uint => (SparkplugDataType.UInt64, Convert.ToUInt64(value)),
        ulong ul => (SparkplugDataType.UInt64, ul),
        float f => (SparkplugDataType.Float, f),
        double d => (SparkplugDataType.Double, d),
        DateTime dt => (SparkplugDataType.DateTime, (ulong)new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds()),
        string s => (SparkplugDataType.String, s),
        char c => (SparkplugDataType.String, c.ToString()),
        _ => (SparkplugDataType.String, value.ToString())
    };

    /// <summary>Value equality used for report-by-exception change detection.</summary>
    public bool ValueEquals(SparkplugMetric other) =>
        DataType == other.DataType && Equals(Value, other.Value);
}
