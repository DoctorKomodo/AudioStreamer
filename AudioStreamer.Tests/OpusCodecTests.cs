using System;
using NAudio.Wave;
using Xunit;

namespace AudioStreamer.Tests
{
    public class OpusCodecTests
    {
        private const int Rate = 48000, Channels = 2, FrameSamples = 480; // 10 ms
        private static readonly WaveFormat Fmt = AudioFormats.ToWaveFormat(Rate, 16, Channels);
        private const int FrameBytes = FrameSamples * Channels * 2;

        private static byte[] SineFrame(double freq)
        {
            var buf = new byte[FrameBytes];
            for (int i = 0; i < FrameSamples; i++)
            {
                short v = (short)(Math.Sin(2 * Math.PI * freq * i / Rate) * 20000);
                for (int ch = 0; ch < Channels; ch++)
                {
                    int idx = (i * Channels + ch) * 2;
                    buf[idx] = (byte)(v & 0xFF);
                    buf[idx + 1] = (byte)((v >> 8) & 0xFF);
                }
            }
            return buf;
        }

        private static double Rms(byte[] b, int count)
        {
            double sum = 0; int n = count / 2;
            for (int i = 0; i < n; i++) { short s = (short)(b[i * 2] | (b[i * 2 + 1] << 8)); sum += (double)s * s; }
            return Math.Sqrt(sum / n);
        }

        [Fact]
        public void RoundTrip_PreservesFrameLengthAndRoughLevel()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            using var dec = CodecFactory.CreateDecoder(CodecId.Opus, Fmt, _ => { });
            var input = SineFrame(440);
            var pcmOut = new byte[FrameBytes * 2];

            byte[]? packet = null; int packetLen = 0;
            enc.Push(input, 0, input.Length, (p, off, len) => { packet = new byte[len]; Buffer.BlockCopy(p, off, packet, 0, len); packetLen = len; });
            Assert.NotNull(packet);
            Assert.True(packetLen > 0 && packetLen < FrameBytes); // actually compressed

            int decodedBytes = dec.Decode(packet!, 0, packetLen, pcmOut);
            Assert.Equal(FrameBytes, decodedBytes);               // same sample count out
            double inRms = Rms(input, FrameBytes), outRms = Rms(pcmOut, decodedBytes);
            Assert.InRange(outRms, inRms * 0.5, inRms * 1.5);     // roughly the same level (lossy)
        }

        [Fact]
        public void Push_AccumulatesPartialBuffersIntoWholeFrames()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            var half = new byte[FrameBytes / 2];
            int emitted = 0;
            enc.Push(half, 0, half.Length, (p, off, len) => emitted++);
            Assert.Equal(0, emitted);                              // half a frame -> nothing yet
            enc.Push(half, 0, half.Length, (p, off, len) => emitted++);
            Assert.Equal(1, emitted);                              // second half completes one frame
        }

        [Fact]
        public void Conceal_UsesLearnedFrameLength()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            using var dec = CodecFactory.CreateDecoder(CodecId.Opus, Fmt, _ => { });
            var pcmOut = new byte[FrameBytes * 2];
            byte[]? packet = null; int packetLen = 0;
            enc.Push(SineFrame(440), 0, FrameBytes, (p, off, len) => { packet = new byte[len]; Buffer.BlockCopy(p, off, packet, 0, len); packetLen = len; });
            dec.Decode(packet!, 0, packetLen, pcmOut);            // decoder learns 480-sample frames
            int concealed = dec.Conceal(pcmOut);
            Assert.Equal(FrameBytes, concealed);                  // PLC synthesises one full frame
        }
    }
}
