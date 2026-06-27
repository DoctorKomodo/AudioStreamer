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
        int LostPerSec)
    {
        /// <summary>Detailed line for the log file / console.</summary>
        public string ToLogLine() => IsReceiver
            ? $"[recv] backlog={BacklogMs:F0}ms pkts/s={PacketsPerSec} KB/s={KbPerSec} lost/s={LostPerSec} overflow/s={OverflowPerSec} resync/s={ResyncPerSec}"
            : $"[send] pkts/s={PacketsPerSec} KB/s={KbPerSec}";

        /// <summary>Compact line for the live UI readout.</summary>
        public string ToCompactLine() => IsReceiver
            ? $"backlog {BacklogMs:F0} ms · {PacketsPerSec} pkt/s · {LostPerSec} lost/s · {ResyncPerSec} resync/s"
            : $"{PacketsPerSec} pkt/s · {KbPerSec} KB/s";
    }
}
