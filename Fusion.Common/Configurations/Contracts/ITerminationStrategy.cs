using System.Buffers;

namespace Fusion.Common.Contracts;

public interface ITerminationStrategy
{
    SequencePosition? FindTerminator(ReadOnlySequence<byte> buffer);
}