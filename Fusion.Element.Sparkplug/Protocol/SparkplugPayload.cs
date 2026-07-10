namespace Fusion.Element.Sparkplug.Protocol;

/// <summary>
/// A Sparkplug B payload (org.eclipse.tahu sparkplug_b.proto, message Payload):
/// timestamp, repeated metrics, and an optional sequence number. Encodes to and
/// decodes from the standard protobuf wire format so it interoperates with
/// Ignition MQTT Engine and any other Sparkplug 3.0 stack.
/// </summary>
public sealed class SparkplugPayload
{
    // Payload field numbers (sparkplug_b.proto)
    private const int FieldTimestamp = 1;
    private const int FieldMetric = 2;
    private const int FieldSeq = 3;

    // Metric field numbers (sparkplug_b.proto, message Payload.Metric)
    private const int MetricName = 1;
    private const int MetricAlias = 2;
    private const int MetricTimestamp = 3;
    private const int MetricDataType = 4;
    private const int MetricIsNull = 7;
    private const int MetricIntValue = 10;
    private const int MetricLongValue = 11;
    private const int MetricFloatValue = 12;
    private const int MetricDoubleValue = 13;
    private const int MetricBooleanValue = 14;
    private const int MetricStringValue = 15;

    public ulong TimestampMs { get; init; } = SparkplugMetric.NowMs();

    /// <summary>Sequence number 0-255. Null for NDEATH payloads, which carry no seq.</summary>
    public ulong? Seq { get; init; }

    public List<SparkplugMetric> Metrics { get; init; } = [];

    public byte[] Encode()
    {
        var writer = new ProtoWriter();
        writer.WriteVarintField(FieldTimestamp, TimestampMs);
        foreach (var metric in Metrics)
            writer.WriteBytesField(FieldMetric, EncodeMetric(metric));
        if (Seq.HasValue)
            writer.WriteVarintField(FieldSeq, Seq.Value);
        return writer.ToArray();
    }

    private static byte[] EncodeMetric(SparkplugMetric metric)
    {
        var writer = new ProtoWriter();
        if (!string.IsNullOrEmpty(metric.Name))
            writer.WriteStringField(MetricName, metric.Name);
        if (metric.Alias.HasValue)
            writer.WriteVarintField(MetricAlias, metric.Alias.Value);
        if (metric.TimestampMs > 0)
            writer.WriteVarintField(MetricTimestamp, metric.TimestampMs);
        writer.WriteVarintField(MetricDataType, (ulong)metric.DataType);

        if (metric.IsNull)
        {
            writer.WriteVarintField(MetricIsNull, 1);
            return writer.ToArray();
        }

        switch (metric.DataType)
        {
            case SparkplugDataType.Int8:
            case SparkplugDataType.Int16:
            case SparkplugDataType.Int32:
                writer.WriteVarintField(MetricIntValue, unchecked((uint)Convert.ToInt32(metric.Value)));
                break;
            case SparkplugDataType.UInt8:
            case SparkplugDataType.UInt16:
            case SparkplugDataType.UInt32:
                writer.WriteVarintField(MetricIntValue, Convert.ToUInt32(metric.Value));
                break;
            case SparkplugDataType.Int64:
                writer.WriteVarintField(MetricLongValue, unchecked((ulong)Convert.ToInt64(metric.Value)));
                break;
            case SparkplugDataType.UInt64:
            case SparkplugDataType.DateTime:
                writer.WriteVarintField(MetricLongValue, Convert.ToUInt64(metric.Value));
                break;
            case SparkplugDataType.Float:
                writer.WriteFixed32Field(MetricFloatValue, BitConverter.SingleToUInt32Bits(Convert.ToSingle(metric.Value)));
                break;
            case SparkplugDataType.Double:
                writer.WriteFixed64Field(MetricDoubleValue, BitConverter.DoubleToUInt64Bits(Convert.ToDouble(metric.Value)));
                break;
            case SparkplugDataType.Boolean:
                writer.WriteVarintField(MetricBooleanValue, Convert.ToBoolean(metric.Value) ? 1UL : 0UL);
                break;
            default: // String, Text, Uuid, Unknown
                writer.WriteStringField(MetricStringValue, metric.Value?.ToString() ?? string.Empty);
                break;
        }

        return writer.ToArray();
    }

    public static SparkplugPayload Decode(byte[] data)
    {
        var reader = new ProtoReader(data);
        ulong timestamp = 0;
        ulong? seq = null;
        var metrics = new List<SparkplugMetric>();

        while (reader.HasMore)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case FieldTimestamp:
                    timestamp = reader.ReadVarint();
                    break;
                case FieldMetric:
                    metrics.Add(DecodeMetric(reader.ReadLengthDelimited()));
                    break;
                case FieldSeq:
                    seq = reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return new SparkplugPayload { TimestampMs = timestamp, Seq = seq, Metrics = metrics };
    }

    private static SparkplugMetric DecodeMetric(byte[] data)
    {
        var reader = new ProtoReader(data);
        string name = string.Empty;
        ulong? alias = null;
        ulong timestamp = 0;
        var dataType = SparkplugDataType.Unknown;
        bool isNull = false;

        // Raw value captured by wire field; interpreted after all fields are read
        // so the value decode does not depend on field ordering.
        uint? rawInt = null;
        ulong? rawLong = null;
        uint? rawFloatBits = null;
        ulong? rawDoubleBits = null;
        bool? rawBool = null;
        string? rawString = null;

        while (reader.HasMore)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case MetricName: name = reader.ReadString(); break;
                case MetricAlias: alias = reader.ReadVarint(); break;
                case MetricTimestamp: timestamp = reader.ReadVarint(); break;
                case MetricDataType: dataType = (SparkplugDataType)reader.ReadVarint(); break;
                case MetricIsNull: isNull = reader.ReadVarint() != 0; break;
                case MetricIntValue: rawInt = (uint)reader.ReadVarint(); break;
                case MetricLongValue: rawLong = reader.ReadVarint(); break;
                case MetricFloatValue: rawFloatBits = reader.ReadFixed32(); break;
                case MetricDoubleValue: rawDoubleBits = reader.ReadFixed64(); break;
                case MetricBooleanValue: rawBool = reader.ReadVarint() != 0; break;
                case MetricStringValue: rawString = reader.ReadString(); break;
                default: reader.SkipField(wireType); break;
            }
        }

        object? value = null;
        if (!isNull)
        {
            value = dataType switch
            {
                SparkplugDataType.Int8 or SparkplugDataType.Int16 or SparkplugDataType.Int32
                    when rawInt.HasValue => (long)unchecked((int)rawInt.Value),
                SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32
                    when rawInt.HasValue => (ulong)rawInt.Value,
                SparkplugDataType.Int64 when rawLong.HasValue => unchecked((long)rawLong.Value),
                SparkplugDataType.UInt64 or SparkplugDataType.DateTime when rawLong.HasValue => rawLong.Value,
                SparkplugDataType.Float when rawFloatBits.HasValue => BitConverter.UInt32BitsToSingle(rawFloatBits.Value),
                SparkplugDataType.Double when rawDoubleBits.HasValue => BitConverter.UInt64BitsToDouble(rawDoubleBits.Value),
                SparkplugDataType.Boolean when rawBool.HasValue => rawBool.Value,
                _ => rawString
            };
        }

        return new SparkplugMetric
        {
            Name = name,
            Alias = alias,
            TimestampMs = timestamp,
            DataType = dataType,
            Value = value
        };
    }
}
