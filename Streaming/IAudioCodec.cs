using System;

namespace AudioStreamer
{
    /// <summary>Turns captured PCM into wire payloads. PCM: MTU slicing. Opus: accumulate a frame, encode.</summary>
    internal interface IAudioEncoder : IDisposable
    {
        // Append captured PCM; invoke emit(payload, offset, length) once per complete output frame (zero or
        // more per call). The offset lets PCM emit straight from the capture buffer with no copy; Opus passes 0.
        void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit);
    }

    /// <summary>Turns a wire payload back into PCM. Conceal() fills a dropped frame (Opus PLC; PCM emits nothing).</summary>
    internal interface IAudioDecoder : IDisposable
    {
        int Decode(byte[] packet, int offset, int count, byte[] pcmOut);  // returns PCM bytes written
        int Conceal(byte[] pcmOut);                                       // returns PCM bytes written (0 for PCM)
    }
}
