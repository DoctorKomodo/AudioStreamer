using System;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;

namespace AudioStreamer
{
    /// <summary>
    /// Opus encoder adapter: accumulates arbitrary WASAPI buffers into whole frames of frameSamples/channel,
    /// then encodes one datagram per frame. All buffers are allocated once — no per-callback allocation.
    /// </summary>
    internal sealed class OpusEncoderAdapter : IAudioEncoder
    {
        private const int Bitrate = 192_000;
        private const int Complexity = 10;

        private readonly OpusEncoder encoder;
        private readonly int frameSamples;
        private readonly byte[] accumulator;   // frameSamples * channels * 2 bytes
        private readonly byte[] encodeBuffer;  // encoded output scratch
        private int accumFill;                 // bytes currently in the accumulator

        public OpusEncoderAdapter(int sampleRate, int channels, int frameSamples)
        {
            this.frameSamples = frameSamples;
            encoder = new OpusEncoder(sampleRate, channels, OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            encoder.SetVbr(true);
            encoder.SetBitRate(Bitrate);          // NOTE capital R
            encoder.SetComplexity(Complexity);
            accumulator = new byte[frameSamples * channels * 2];
            encodeBuffer = new byte[WireProtocol.MaxUdpAudioBytes]; // one Opus frame is far smaller
        }

        public void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit)
        {
            int pos = offset, end = offset + count;
            while (pos < end)
            {
                int take = Math.Min(accumulator.Length - accumFill, end - pos);
                Buffer.BlockCopy(pcm, pos, accumulator, accumFill, take);
                accumFill += take;
                pos += take;
                if (accumFill == accumulator.Length)
                {
                    int len = encoder.Encode(accumulator, frameSamples, encodeBuffer, encodeBuffer.Length);
                    emit(encodeBuffer, 0, len);   // Opus payload starts at 0 of its own encode buffer
                    accumFill = 0;
                }
            }
        }

        public void Dispose() => encoder.Dispose();
    }

    /// <summary>
    /// Opus decoder adapter. Learns the stream's frame length from the first successful decode and reuses it
    /// for concealment (PLC needs a sample count, and the sender's frame size is not signalled but is constant).
    /// </summary>
    internal sealed class OpusDecoderAdapter : IAudioDecoder
    {
        private readonly OpusDecoder decoder;
        private readonly int channels;
        private readonly int maxFrameSamples;   // decode-output capacity, samples/channel (20 ms)
        private readonly byte[] inScratch;      // payload copied to offset 0 (OpusSharp Decode has no offset arg)
        private int lastFrameSamples;

        public OpusDecoderAdapter(int sampleRate, int channels)
        {
            this.channels = channels;
            decoder = new OpusDecoder(sampleRate, channels);
            maxFrameSamples = sampleRate / 50;              // 20 ms = largest supported frame
            lastFrameSamples = sampleRate / 100;            // 10 ms default until the first decode
            inScratch = new byte[WireProtocol.MaxUdpAudioBytes];
        }

        public int Decode(byte[] packet, int offset, int count, byte[] pcmOut)
        {
            Buffer.BlockCopy(packet, offset, inScratch, 0, count);
            int samples = decoder.Decode(inScratch, count, pcmOut, maxFrameSamples, false);
            lastFrameSamples = samples;
            return samples * channels * 2;
        }

        public int Conceal(byte[] pcmOut)
        {
            // Null input packet => Opus PLC; frame size must match the stream (learned above).
            int samples = decoder.Decode((byte[]?)null, 0, pcmOut, lastFrameSamples, false);
            return samples * channels * 2;
        }

        public void Dispose() => decoder.Dispose();
    }
}
