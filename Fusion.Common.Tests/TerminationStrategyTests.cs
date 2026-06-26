using System.Buffers;
using System.Text;
using Fusion.Common.TCP_Classes;
using Xunit;

namespace Fusion.Common.Tests
{
    public class TerminationStrategyTests
    {
        [Fact]
        public void DelimiterSetStrategy_ShouldFindTerminatorInSingleSegment()
        {
            var strategy = new DelimiterSetStrategy(0x03); // ETX
            var data = Encoding.ASCII.GetBytes("Hello\x03World");
            var buffer = new ReadOnlySequence<byte>(data);

            var position = strategy.FindTerminator(buffer);

            Assert.NotNull(position);
            var message = buffer.Slice(0, position.Value);
            Assert.Equal("Hello\x03", Encoding.ASCII.GetString(message.ToArray()));
        }

        [Fact]
        public void DelimiterSetStrategy_ShouldFindTerminatorInMultiSegment()
        {
            var strategy = new DelimiterSetStrategy(0x03); // ETX

            // Create a multi-segment sequence
            var segment1 = Encoding.ASCII.GetBytes("Part1");
            var segment2 = Encoding.ASCII.GetBytes("Part2\x03More");

            var firstSegment = new BufferSegment(segment1);
            var secondSegment = firstSegment.Append(segment2);

            var buffer = new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, secondSegment.Memory.Length);

            var position = strategy.FindTerminator(buffer);

            Assert.NotNull(position);
            var message = buffer.Slice(0, position.Value);
            Assert.Equal("Part1Part2\x03", Encoding.ASCII.GetString(message.ToArray()));
        }

        private class BufferSegment : ReadOnlySequenceSegment<byte>
        {
            public BufferSegment(Memory<byte> memory)
            {
                Memory = memory;
            }

            public BufferSegment Append(Memory<byte> memory)
            {
                var nextSegment = new BufferSegment(memory)
                {
                    RunningIndex = RunningIndex + Memory.Length
                };
                Next = nextSegment;
                return nextSegment;
            }
        }
    }
}
