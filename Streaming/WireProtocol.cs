namespace AudioStreamer
{
    /// <summary>
    /// The on-the-wire UDP framing shared by sender and receiver. Every datagram is a 2-byte header
    /// (1-byte format code — an index into AudioFormats.Formats — plus a 1-byte wrapping sequence
    /// number) followed by raw PCM audio.
    /// </summary>
    internal static class WireProtocol
    {
        public const int HeaderBytes = 2;          // total header (audio starts here on both sides)
        public const int FormatHeaderBytes = 1;    // byte 0: format code (index into AudioFormats.Formats)
        public const int SequenceByteOffset = 1;   // byte 1: wrapping per-datagram sequence number

        // Keep each datagram inside a 1500-byte Ethernet MTU (minus IP/UDP/header, with VPN/PPPoE headroom)
        // so IP never fragments the audio. Audio is sliced on whole-frame boundaries by the sender.
        public const int MaxUdpAudioBytes = 1440;
        // Roomy socket buffers absorb scheduling jitter/bursts so the kernel doesn't silently drop datagrams.
        public const int SocketBufferBytes = 1 << 20; // 1 MiB
        // Winsock ioctl that stops a UDP socket's Receive/Send from throwing 10054 (WSAECONNRESET) after a
        // prior send drew an ICMP "port unreachable" (e.g. the sender started before the receiver bound).
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        /// <summary>Writes the 1-byte format code into dest[0].</summary>
        public static void WriteFormatCode(byte[] dest, byte code) => dest[0] = code;

        /// <summary>Reads the 1-byte format code from src[0].</summary>
        public static byte ReadFormatCode(byte[] src) => src[0];
    }
}
