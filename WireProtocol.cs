namespace AudioStreamer
{
    /// <summary>
    /// The on-the-wire UDP framing shared by sender and receiver. Every datagram is a 4-byte header
    /// (3-byte wave-format descriptor + 1-byte wrapping sequence number) followed by raw PCM audio.
    /// </summary>
    internal static class WireProtocol
    {
        public const int HeaderBytes = 4;          // total header (audio starts here on both sides)
        public const int FormatHeaderBytes = 3;    // bytes 0-2: packed wave format
        public const int SequenceByteOffset = 3;   // byte 3: wrapping per-datagram sequence number

        // Keep each datagram inside a 1500-byte Ethernet MTU (minus IP/UDP/header, with VPN/PPPoE headroom)
        // so IP never fragments the audio. Audio is sliced on whole-frame boundaries by the sender.
        public const int MaxUdpAudioBytes = 1440;
        // Roomy socket buffers absorb scheduling jitter/bursts so the kernel doesn't silently drop datagrams.
        public const int SocketBufferBytes = 1 << 20; // 1 MiB
        // Winsock ioctl that stops a UDP socket's Receive/Send from throwing 10054 (WSAECONNRESET) after a
        // prior send drew an ICMP "port unreachable" (e.g. the sender started before the receiver bound).
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        /// <summary>Writes the 3-byte format descriptor into dest[0..2]. No allocation.</summary>
        public static void WriteFormatHeader(byte[] dest, int sampleRate, int bitDepth, int channels)
        {
            dest[0] = (byte)(sampleRate / 1000);
            dest[1] = (byte)bitDepth;
            dest[2] = (byte)channels;
        }

        /// <summary>Reads the 3-byte format descriptor from src[0..2].</summary>
        public static (int sampleRate, int bitDepth, int channels) ReadFormatHeader(byte[] src) =>
            (src[0] * 1000, src[1], src[2]);
    }
}
