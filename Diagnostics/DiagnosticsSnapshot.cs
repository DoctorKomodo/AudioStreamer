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
        double MinBacklogMs)
    {
        /// <summary>Factory for a sender snapshot (no receiver-side metrics).</summary>
        public static DiagnosticsSnapshot ForSender(int packetsPerSec, long kbPerSec) =>
            new(false, 0, packetsPerSec, kbPerSec, 0, 0, 0, 0, 0, 0);

        /// <summary>Factory for a receiver snapshot.</summary>
        public static DiagnosticsSnapshot ForReceiver(double backlogMs, int packetsPerSec, long kbPerSec, int overflowPerSec, int resyncPerSec, int lostPerSec, int reorderPerSec, int underrunPerSec, double minBacklogMs) =>
            new(true, backlogMs, packetsPerSec, kbPerSec, overflowPerSec, resyncPerSec, lostPerSec, reorderPerSec, underrunPerSec, minBacklogMs);

        /// <summary>Detailed line for the log file / console.</summary>
        public string ToLogLine() => IsReceiver
            ? $"[recv] backlog={BacklogMs:F0}ms min={MinBacklogMs:F0}ms pkts/s={PacketsPerSec} KB/s={KbPerSec} lost/s={LostPerSec} reorder/s={ReorderPerSec} underrun/s={UnderrunPerSec} overflow/s={OverflowPerSec} resync/s={ResyncPerSec}"
            : $"[send] pkts/s={PacketsPerSec} KB/s={KbPerSec}";

        /// <summary>Compact, vertically-stacked readout for the live UI (grouped: latency / wire order / buffer drift).</summary>
        public string ToCompactLine() => IsReceiver
            ? $"backlog {BacklogMs:F0} ms · min {MinBacklogMs:F0}\n"
            + $"{LostPerSec} lost/s · {ReorderPerSec} reorder/s\n"
            + $"{UnderrunPerSec} underrun/s · {ResyncPerSec} resync/s"
            : $"{PacketsPerSec} pkt/s · {KbPerSec} KB/s";
    }
}
