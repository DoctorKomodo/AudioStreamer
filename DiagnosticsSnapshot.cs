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
        int UnderrunPerSec,
        double MinBacklogMs)
    {
        /// <summary>Detailed line for the log file / console.</summary>
        public string ToLogLine() => IsReceiver
            ? $"[recv] backlog={BacklogMs:F0}ms min={MinBacklogMs:F0}ms pkts/s={PacketsPerSec} KB/s={KbPerSec} lost/s={LostPerSec} underrun/s={UnderrunPerSec} overflow/s={OverflowPerSec} resync/s={ResyncPerSec}"
            : $"[send] pkts/s={PacketsPerSec} KB/s={KbPerSec}";

        /// <summary>Compact line for the live UI readout.</summary>
        public string ToCompactLine() => IsReceiver
            ? $"backlog {BacklogMs:F0} ms (min {MinBacklogMs:F0}) · {LostPerSec} lost/s · {UnderrunPerSec} underrun/s · {ResyncPerSec} resync/s"
            : $"{PacketsPerSec} pkt/s · {KbPerSec} KB/s";
    }
}
