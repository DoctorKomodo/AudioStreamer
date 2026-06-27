using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>
    /// Transparent pass-through over a <see cref="BufferedWaveProvider"/> that counts playback underruns —
    /// render-thread reads the buffer can't fully satisfy. On an underrun the buffer zero-fills the shortfall
    /// (a brief silence — the audible "drop"), so it is invisible to the receive loop: <see
    /// cref="BufferedWaveProvider.Read"/> with ReadFully always returns the full count, and only the render
    /// thread sees the gap. This wrapper sits exactly where it can observe it, adding one comparison plus an
    /// occasional atomic increment per render callback — no allocation, no extra buffering, no added latency.
    /// </summary>
    public sealed class UnderrunCountingWaveProvider : IWaveProvider
    {
        private readonly BufferedWaveProvider source;
        private int underruns;

        public UnderrunCountingWaveProvider(BufferedWaveProvider source) => this.source = source;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            // Checked before the read: ReadFully zero-fills the shortfall and returns the full count, so the
            // return value alone can't reveal an underrun.
            if (source.BufferedBytes < count)
                Interlocked.Increment(ref underruns);
            return source.Read(buffer, offset, count);
        }

        /// <summary>Atomically returns the underrun count accumulated since the last call and resets it to zero.</summary>
        public int ExchangeUnderruns() => Interlocked.Exchange(ref underruns, 0);
    }
}
