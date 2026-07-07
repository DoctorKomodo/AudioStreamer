using System.Collections.Generic;
using NAudio.Wave;
using Xunit;

namespace AudioStreamer.Tests
{
    public class PcmCodecTests
    {
        private static readonly WaveFormat Fmt = AudioFormats.ToWaveFormat(48000, 16, 2); // BlockAlign = 4

        [Fact]
        public void Encoder_SlicesOnWholeFrames_UnderMtu()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Pcm, Fmt, 0, _ => { });
            var pcm = new byte[WireProtocol.MaxUdpAudioBytes + 400]; // one full slice + a remainder
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)i;
            var slices = new List<int>();
            enc.Push(pcm, 0, pcm.Length, (payload, off, len) => slices.Add(len));

            int maxChunk = (WireProtocol.MaxUdpAudioBytes / Fmt.BlockAlign) * Fmt.BlockAlign; // 1440
            Assert.Equal(new[] { maxChunk, pcm.Length - maxChunk }, slices);
            Assert.All(slices, len => Assert.Equal(0, len % Fmt.BlockAlign)); // frame-aligned
        }

        [Fact]
        public void Decoder_IsBitExactIdentity()
        {
            using var dec = CodecFactory.CreateDecoder(CodecId.Pcm, Fmt, _ => { });
            var packet = new byte[3 + 8];
            for (int i = 0; i < packet.Length; i++) packet[i] = (byte)(i * 7);
            var outBuf = new byte[16];
            int n = dec.Decode(packet, 3, 8, outBuf);
            Assert.Equal(8, n);
            for (int i = 0; i < 8; i++) Assert.Equal(packet[3 + i], outBuf[i]);
        }

        [Fact]
        public void Decoder_ConcealEmitsNothing()
        {
            using var dec = CodecFactory.CreateDecoder(CodecId.Pcm, Fmt, _ => { });
            Assert.Equal(0, dec.Conceal(new byte[16]));
        }
    }
}
