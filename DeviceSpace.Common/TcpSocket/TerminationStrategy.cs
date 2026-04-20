using System.Text;
using DeviceSpace.Common.Contracts;

namespace DeviceSpace.Common.TCP_Classes;

/// <summary>
/// Terminiator that is more than 1 character long.
/// </summary>
public class SequenceTerminationStrategy(params byte[][]? terminators) : ITerminationStrategy
{
    private readonly List<byte[]> _terminators = terminators is { Length: > 0 } 
        ? terminators.ToList() 
        :
        [ ];

    public bool IsMessageComplete(ReadOnlySpan<byte> buffer, byte lastByte)
    {
        if (buffer.Length == 0) return false;

        foreach (var term in _terminators)
        {
            if (lastByte != term[^1]) continue;
            if (buffer.Length < term.Length) continue;

            bool match = true;
            for (int i = 0; i < term.Length; i++)
            {
                if (buffer[buffer.Length - 1 - i] != term[term.Length - 1 - i])
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }
}

public class DelimiterSetStrategy : ITerminationStrategy
{
    private readonly HashSet<byte> _delimiters;

    public DelimiterSetStrategy(params byte[]? delimiters)
    {
        _delimiters = new HashSet<byte>(delimiters ?? new byte[] { 0x03 });
    }

    public bool IsMessageComplete(ReadOnlySpan<byte> buffer, byte lastByte)
    {
        return _delimiters.Contains(lastByte);
    }
}

public class FixedLengthTerminationStrategy(int length) : ITerminationStrategy
{
    private readonly int _fixedLength = length;

    public bool IsMessageComplete(ReadOnlySpan<byte> buffer, byte lastByte)
    {
        return buffer.Length >= _fixedLength;
    }
}
