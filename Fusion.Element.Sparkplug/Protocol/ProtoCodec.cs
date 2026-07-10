using System.Text;

namespace Fusion.Element.Sparkplug.Protocol;

/// <summary>
/// Minimal protobuf wire-format writer used to encode Sparkplug B payloads
/// (org.eclipse.tahu sparkplug_b.proto) without an external protobuf dependency.
/// Supports the wire types Sparkplug B actually uses: varint, fixed32, fixed64,
/// and length-delimited.
/// </summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteVarintField(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, 0);
        WriteVarint(value);
    }

    public void WriteFixed32Field(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, 5);
        Span<byte> buf = stackalloc byte[4];
        BitConverter.TryWriteBytes(buf, value);
        _stream.Write(buf);
    }

    public void WriteFixed64Field(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, 1);
        Span<byte> buf = stackalloc byte[8];
        BitConverter.TryWriteBytes(buf, value);
        _stream.Write(buf);
    }

    public void WriteStringField(int fieldNumber, string value)
    {
        WriteBytesField(fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    public void WriteBytesField(int fieldNumber, byte[] value)
    {
        WriteTag(fieldNumber, 2);
        WriteVarint((ulong)value.Length);
        _stream.Write(value, 0, value.Length);
    }

    private void WriteTag(int fieldNumber, int wireType) =>
        WriteVarint((ulong)((fieldNumber << 3) | wireType));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>
/// Minimal protobuf wire-format reader, counterpart of <see cref="ProtoWriter"/>.
/// Unknown fields are skipped so payloads produced by richer Sparkplug stacks
/// (Ignition MQTT Engine, Tahu) decode cleanly.
/// </summary>
internal sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _pos;

    public ProtoReader(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    public bool HasMore => _pos < _data.Length;

    public (int FieldNumber, int WireType) ReadTag()
    {
        ulong tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _data.Length)
                throw new InvalidDataException("Truncated varint in Sparkplug payload.");
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Varint too long in Sparkplug payload.");
        }
    }

    public uint ReadFixed32()
    {
        EnsureAvailable(4);
        uint v = BitConverter.ToUInt32(_data, _pos);
        _pos += 4;
        return v;
    }

    public ulong ReadFixed64()
    {
        EnsureAvailable(8);
        ulong v = BitConverter.ToUInt64(_data, _pos);
        _pos += 8;
        return v;
    }

    public byte[] ReadLengthDelimited()
    {
        int length = (int)ReadVarint();
        EnsureAvailable(length);
        var result = new byte[length];
        Array.Copy(_data, _pos, result, 0, length);
        _pos += length;
        return result;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: EnsureAvailable(8); _pos += 8; break;
            case 2: ReadLengthDelimited(); break;
            case 5: EnsureAvailable(4); _pos += 4; break;
            default: throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    private void EnsureAvailable(int count)
    {
        if (_pos + count > _data.Length)
            throw new InvalidDataException("Truncated Sparkplug payload.");
    }
}
