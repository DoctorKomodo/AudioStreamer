using Xunit;

namespace AudioStreamer.Tests
{
    public class WireProtocolTests
    {
        [Fact]
        public void Offsets_AreDistinctAndHeaderIsThree()
        {
            Assert.Equal(0, WireProtocol.FormatHeaderBytes - 1); // format code at 0
            Assert.Equal(1, WireProtocol.CodecByteOffset);
            Assert.Equal(2, WireProtocol.SequenceByteOffset);
            Assert.Equal(3, WireProtocol.HeaderBytes);
        }

        [Fact]
        public void FormatCode_Codec_Sequence_RoundTripIndependently()
        {
            var buf = new byte[WireProtocol.HeaderBytes + 4];
            WireProtocol.WriteFormatCode(buf, 42);
            WireProtocol.WriteCodec(buf, CodecId.Opus);
            buf[WireProtocol.SequenceByteOffset] = 200;

            Assert.Equal(42, WireProtocol.ReadFormatCode(buf));
            Assert.Equal(CodecId.Opus, WireProtocol.ReadCodec(buf));
            Assert.Equal(200, buf[WireProtocol.SequenceByteOffset]);
        }

        [Fact]
        public void ReadCodec_DefaultsToPcmForZero()
        {
            var buf = new byte[WireProtocol.HeaderBytes];
            Assert.Equal(CodecId.Pcm, WireProtocol.ReadCodec(buf));
        }
    }
}
