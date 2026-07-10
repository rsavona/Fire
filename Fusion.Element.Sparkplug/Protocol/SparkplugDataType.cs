namespace Fusion.Element.Sparkplug.Protocol;

/// <summary>
/// Sparkplug B metric data type codes as defined by the Sparkplug 3.0
/// specification (sparkplug_b.proto, DataType enum).
/// </summary>
public enum SparkplugDataType : uint
{
    Unknown = 0,
    Int8 = 1,
    Int16 = 2,
    Int32 = 3,
    Int64 = 4,
    UInt8 = 5,
    UInt16 = 6,
    UInt32 = 7,
    UInt64 = 8,
    Float = 9,
    Double = 10,
    Boolean = 11,
    String = 12,
    DateTime = 13,
    Text = 14,
    Uuid = 15
}
