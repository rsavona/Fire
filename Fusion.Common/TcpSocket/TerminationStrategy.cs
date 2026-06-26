using System.Buffers;
using System.Text;
using Fusion.Common.Contracts;

namespace Fusion.Common.TCP_Classes;

/// <summary>
/// Terminator that can be multiple bytes long. Scans the entire sequence for a match.
/// </summary>
public class SequenceTerminationStrategy(params byte[][]? terminators) : ITerminationStrategy
{
    private readonly List<byte[]> _terminators = terminators is { Length: > 0 } 
        ? terminators.ToList() 
        : [ ];

    public SequencePosition? FindTerminator(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsEmpty) return null;

        foreach (var term in _terminators)
        {
            var result = FindSequence(buffer, term);
            if (result.HasValue) return result;
        }

        return null;
    }

    private static SequencePosition? FindSequence(ReadOnlySequence<byte> buffer, byte[] sequence)
    {
        if (sequence.Length == 0) return null;
        if (sequence.Length == 1)
        {
            var pos = buffer.PositionOf(sequence[0]);
            return pos.HasValue ? buffer.GetPosition(1, pos.Value) : null;
        }

        var firstByte = sequence[0];
        var position = buffer.Start;

        while (true)
        {
            var found = buffer.Slice(position).PositionOf(firstByte);
            if (found == null) return null;

            // Found first byte, check the rest
            var remaining = buffer.Slice(found.Value);
            if (remaining.Length < sequence.Length) return null;

            if (IsMatch(remaining, sequence))
            {
                // Return position AFTER the sequence
                return buffer.GetPosition(sequence.Length, found.Value);
            }

            // Move past this occurrence and keep looking
            position = buffer.GetPosition(1, found.Value);
        }
    }

    private static bool IsMatch(ReadOnlySequence<byte> buffer, byte[] sequence)
    {
        if (buffer.Length < sequence.Length) return false;
        
        int i = 0;
        foreach (var segment in buffer)
        {
            var span = segment.Span;
            for (int j = 0; j < span.Length; j++)
            {
                if (span[j] != sequence[i]) return false;
                if (++i == sequence.Length) return true;
            }
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

    public SequencePosition? FindTerminator(ReadOnlySequence<byte> buffer)
    {
        var current = buffer.Start;
        var next = current;
        while (buffer.TryGet(ref next, out var memory))
        {
            var span = memory.Span;
            for (int i = 0; i < span.Length; i++)
            {
                if (_delimiters.Contains(span[i]))
                {
                    var absolutePos = buffer.GetPosition(i, current); // Position AT the delimiter
                    return buffer.GetPosition(1, absolutePos); // Position AFTER the delimiter
                }
            }
            current = next;
        }
        return null;
    }
}

public class FixedLengthTerminationStrategy(int length) : ITerminationStrategy
{
    private readonly int _fixedLength = length;

    public SequencePosition? FindTerminator(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length >= _fixedLength)
        {
            return buffer.GetPosition(_fixedLength);
        }
        return null;
    }
}
