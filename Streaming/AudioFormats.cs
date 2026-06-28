using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>
    /// The catalog of selectable audio formats — single source of truth shared by the sender UI
    /// (dropdowns), SenderSession (format -> wire code), and ReceiverSession (wire code -> format +
    /// validity guard). The wire carries a 1-byte index into <see cref="Formats"/>; that ordering is
    /// part of the wire contract, so both ends must run the same build (already required — see WireProtocol).
    /// </summary>
    public static class AudioFormats
    {
        public static readonly int[] SampleRates   = { 44100, 48000, 88200, 96000, 176400, 192000 };
        public static readonly int[] BitDepths     = { 16, 24, 32 };
        public static readonly int[] ChannelCounts = { 1, 2, 6, 8 };

        public const int DefaultSampleRate = 48000;
        public const int DefaultBitDepth   = 16;
        public const int DefaultChannels   = 2;

        public readonly record struct Format(int SampleRate, int BitDepth, int Channels);

        /// <summary>
        /// Canonical ordered table: the cartesian product of the three arrays, sample-rate-major then
        /// bit-depth then channels. A format's position here IS its wire code, generated identically on
        /// both ends. 6 × 3 × 4 = 72 entries (fits one byte).
        /// </summary>
        public static readonly IReadOnlyList<Format> Formats = Build();

        private static Format[] Build()
        {
            var list = new List<Format>(SampleRates.Length * BitDepths.Length * ChannelCounts.Length);
            foreach (int rate in SampleRates)
                foreach (int depth in BitDepths)
                    foreach (int ch in ChannelCounts)
                        list.Add(new Format(rate, depth, ch));
            return list.ToArray();
        }

        /// <summary>
        /// Format -> wire code. Falls back to the default format's code (no throw) if the combo isn't in
        /// the table — a safety net for a hand-edited config.json; the dropdowns otherwise guarantee a
        /// valid combo.
        /// </summary>
        public static byte ToCode(int sampleRate, int bitDepth, int channels)
        {
            int idx = IndexOf(sampleRate, bitDepth, channels);
            if (idx < 0)
                idx = IndexOf(DefaultSampleRate, DefaultBitDepth, DefaultChannels);
            return (byte)idx;
        }

        /// <summary>Wire code -> format, or null if the code is out of range (the receiver guard).</summary>
        public static Format? FromCode(byte code) => code < Formats.Count ? Formats[code] : null;

        /// <summary>
        /// Builds the NAudio WaveFormat for a catalog format. 32-bit maps to IEEE float
        /// (CreateIeeeFloatWaveFormat) — NAudio's int-args WaveFormat ctor would produce 32-bit *integer*
        /// PCM, which WASAPI shared mode rejects on typical hardware (its mix format is 32-bit float).
        /// 16/24-bit are genuine integer PCM. Both ends must build the format the same way, so this is the
        /// single construction point.
        /// </summary>
        public static WaveFormat ToWaveFormat(int sampleRate, int bitDepth, int channels) =>
            bitDepth == 32
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
                : new WaveFormat(sampleRate, bitDepth, channels);

        private static int IndexOf(int sampleRate, int bitDepth, int channels)
        {
            for (int i = 0; i < Formats.Count; i++)
            {
                Format f = Formats[i];
                if (f.SampleRate == sampleRate && f.BitDepth == bitDepth && f.Channels == channels)
                    return i;
            }
            return -1;
        }

        // Raw Hz, matching what Windows shows natively (Sound > Advanced lists "44100 Hz"). Default integer
        // formatting inserts no group separators (grouping is opt-in via "N"/custom formats), so this is
        // locale-safe with no InvariantCulture needed — and it sidesteps the decimal-separator issue that
        // a "44.1 kHz" form would have on comma-decimal locales.
        public static string RateLabel(int hz) => $"{hz} Hz";

        public static string DepthLabel(int bits) => $"{bits}-bit";

        public static string ChannelLabel(int ch) => ch switch
        {
            1 => "1 (Mono)",
            2 => "2 (Stereo)",
            6 => "6 (5.1)",
            8 => "8 (7.1)",
            _ => ch.ToString()
        };
    }
}
