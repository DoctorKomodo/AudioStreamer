using System.Collections.Generic;
using Xunit;

namespace AudioStreamer.Tests
{
    public class ReorderBufferTests
    {
        private static byte[] One(byte v) => new[] { v };

        [Fact]
        public void InOrder_EmitsStraightThrough_NoLoss()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(4, (b, o, c) => emitted.Add(b[o]), () => lost++);
            for (byte s = 0; s < 5; s++) rb.Add(s, One(s), 0, 1);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, emitted);
            Assert.Equal(0, lost);
        }

        [Fact]
        public void SingleGap_FiresOnLostOncePerMissingSequence()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]), () => lost++);
            rb.Add(0, One(0), 0, 1);          // in order
            rb.Add(2, One(2), 0, 1);          // 1 missing, held: {2}
            rb.Add(3, One(3), 0, 1);          // held {2,3} >= maxDepth(2) -> give up on 1
            Assert.Equal(1, lost);            // exactly one skip
            Assert.Equal(new byte[] { 0, 2, 3 }, emitted);
        }

        [Fact]
        public void MultiGap_FiresOnLostPerSkippedSequence()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]), () => lost++);
            rb.Add(0, One(0), 0, 1);          // in order
            rb.Add(3, One(3), 0, 1);          // 1,2 missing; held {3}
            rb.Add(4, One(4), 0, 1);          // held {3,4} >= 2 -> skip 1, then skip 2, emit 3,4
            Assert.Equal(2, lost);            // two skipped sequences -> two onLost calls
            Assert.Equal(new byte[] { 0, 3, 4 }, emitted);
        }

        [Fact]
        public void NullOnLost_IsAllowed_PcmCallSiteUnchanged()
        {
            var emitted = new List<byte>();
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]));  // no onLost
            rb.Add(0, One(0), 0, 1);
            rb.Add(2, One(2), 0, 1);
            rb.Add(3, One(3), 0, 1);          // skip 1 with null onLost must not throw
            Assert.Equal(new byte[] { 0, 2, 3 }, emitted);
        }
    }
}
