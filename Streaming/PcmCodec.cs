using System;

namespace AudioStreamer
{
    internal sealed class PcmEncoderAdapter : IAudioEncoder
    {
        private readonly int maxChunk;

        public PcmEncoderAdapter(int blockAlign)
        {
            maxChunk = Math.Max(blockAlign, (WireProtocol.MaxUdpAudioBytes / blockAlign) * blockAlign);
        }

        public void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit)
        {
            for (int pos = 0; pos < count; pos += maxChunk)
            {
                int chunk = Math.Min(maxChunk, count - pos);
                emit(pcm, offset + pos, chunk);   // straight from the capture buffer — no copy
            }
        }

        public void Dispose() { }
    }

    internal sealed class PcmDecoderAdapter : IAudioDecoder
    {
        public int Decode(byte[] packet, int offset, int count, byte[] pcmOut)
        {
            Buffer.BlockCopy(packet, offset, pcmOut, 0, count);
            return count;
        }

        public int Conceal(byte[] pcmOut) => 0;   // today's "skip the gap" behaviour

        public void Dispose() { }
    }
}
