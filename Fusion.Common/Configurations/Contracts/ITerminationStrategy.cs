namespace Fusion.Common.Contracts;

public interface ITerminationStrategy
{
    bool IsMessageComplete(ReadOnlySpan<byte> buffer, byte lastByte);
}