namespace DeviceSpace.Common.Contracts;

public interface ITerminationStrategy
{
    bool IsMessageComplete(ReadOnlySpan<byte> buffer, byte lastByte);
}