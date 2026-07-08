namespace AudioStreamer
{
    /// <summary>Codec of a datagram's payload. Public + top-level so the persisted Config.Codec can expose it.</summary>
    public enum CodecId : byte { Pcm = 0, Opus = 1 }

    /// <summary>
    /// On-the-wire UDP framing shared by sender and receiver. Every datagram is a 3-byte header
    /// (byte 0: format code — an index into AudioFormats.Formats; byte 1: codec id; byte 2: wrapping
    /// per-datagram sequence number) followed by the payload (raw PCM, or one Opus frame).
    /// </summary>
    internal static class WireProtocol
    {
        public const int HeaderBytes = 3;          // total header (payload starts here on both sides)
        public const int FormatHeaderBytes = 1;    // byte 0: format code (index into AudioFormats.Formats)
        public const int CodecByteOffset = 1;      // byte 1: codec id
        public const int SequenceByteOffset = 2;   // byte 2: wrapping per-datagram sequence number

        // Keep each datagram inside a 1500-byte Ethernet MTU (minus IP/UDP/header, with VPN/PPPoE headroom)
        // so IP never fragments the audio. PCM is sliced on whole-frame boundaries by the sender.
        public const int MaxUdpAudioBytes = 1440;
        public const int SocketBufferBytes = 1 << 20; // 1 MiB
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        public static void WriteFormatCode(byte[] dest, byte code) => dest[0] = code;
        public static byte ReadFormatCode(byte[] src) => src[0];

        public static void WriteCodec(byte[] dest, CodecId c) => dest[CodecByteOffset] = (byte)c;
        public static CodecId ReadCodec(byte[] src) => (CodecId)src[CodecByteOffset];
    }
}
