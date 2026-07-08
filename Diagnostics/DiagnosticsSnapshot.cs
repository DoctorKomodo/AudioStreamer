namespace AudioStreamer
{
    /// <summary>One second of streaming diagnostics, with both output formats.</summary>
    public readonly record struct DiagnosticsSnapshot(
        bool IsReceiver,
        double BacklogMs,
        int PacketsPerSec,
        long KbPerSec,
        int OverflowPerSec,
        int ResyncPerSec,
        int LostPerSec,
        int ReorderPerSec,
        int UnderrunPerSec,
        double MinBacklogMs,
        CodecId Codec)
    {
        // The active codec: on the sender it's the configured codec; on the receiver it's the codec
        // auto-detected from the incoming datagrams (the wire codec, which the config doesn't dictate).
        private string CodecLabel => Codec.ToString().ToLowerInvariant();

        /// <summary>Factory for a sender snapshot (no receiver-side metrics).</summary>
        public static DiagnosticsSnapshot ForSender(int packetsPerSec, long kbPerSec, CodecId codec) =>
            new(false, 0, packetsPerSec, kbPerSec, 0, 0, 0, 0, 0, 0, codec);

        /// <summary>Factory for a receiver snapshot.</summary>
        public static DiagnosticsSnapshot ForReceiver(double backlogMs, int packetsPerSec, long kbPerSec, int overflowPerSec, int resyncPerSec, int lostPerSec, int reorderPerSec, int underrunPerSec, double minBacklogMs, CodecId codec) =>
            new(true, backlogMs, packetsPerSec, kbPerSec, overflowPerSec, resyncPerSec, lostPerSec, reorderPerSec, underrunPerSec, minBacklogMs, codec);

        /// <summary>Detailed line for the log file / console.</summary>
        public string ToLogLine() => IsReceiver
            ? $"[recv] codec={CodecLabel} backlog={BacklogMs:F0}ms min={MinBacklogMs:F0}ms pkts/s={PacketsPerSec} KB/s={KbPerSec} lost/s={LostPerSec} reorder/s={ReorderPerSec} underrun/s={UnderrunPerSec} overflow/s={OverflowPerSec} resync/s={ResyncPerSec}"
            : $"[send] codec={CodecLabel} pkts/s={PacketsPerSec} KB/s={KbPerSec}";

        /// <summary>Compact, vertically-stacked readout for the live UI (codec / latency / wire order / buffer drift).</summary>
        public string ToCompactLine() => IsReceiver
            ? $"codec {CodecLabel}\n"
            + $"backlog {BacklogMs:F0} ms · min {MinBacklogMs:F0}\n"
            + $"{LostPerSec} lost/s · {ReorderPerSec} reorder/s\n"
            + $"{UnderrunPerSec} underrun/s · {ResyncPerSec} resync/s"
            : $"codec {CodecLabel}\n"
            + $"{PacketsPerSec} pkt/s · {KbPerSec} KB/s";
    }
}
