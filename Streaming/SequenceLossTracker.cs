namespace AudioStreamer
{
    /// <summary>
    /// Classifies gaps in the per-datagram sequence number as either <b>reordering</b> (a skipped number that
    /// arrives late) or <b>true loss</b> (a number that never shows up within a small window). The wire
    /// sequence is a single byte; it is unwrapped to a continuous counter relative to the next-expected value,
    /// assuming any jump stays within ±128 (i.e. fewer than 128 consecutive drops). Runs on the receive
    /// thread and is allocation-free in the in-order steady state — only an actual gap touches the pending set.
    /// </summary>
    public sealed class SequenceLossTracker
    {
        // A skipped number is declared lost once this many newer datagrams have passed without it arriving.
        // 32 packets ≈ 160 ms at 200 pkt/s: far longer than any plausible LAN reordering, far shorter than the
        // 128-packet unwrap limit.
        private const int Window = 32;

        private long nextExpected = -1;                    // unwrapped; -1 until the first datagram seeds it
        private readonly SortedSet<long> pending = new();  // unwrapped seqs skipped, still awaiting arrival
        private int reorders;
        private int losses;

        public void OnReceived(byte seq)
        {
            if (nextExpected < 0)
            {
                nextExpected = seq + 1;   // first datagram defines the baseline; nothing to compare against yet
                return;
            }

            // Unwrap the byte to the continuous value nearest nextExpected.
            int delta = (seq - (int)(nextExpected & 0xFF)) & 0xFF;
            long unwrapped = delta < 128 ? nextExpected + delta : nextExpected - (256 - delta);

            if (unwrapped == nextExpected)
            {
                nextExpected++;
            }
            else if (unwrapped > nextExpected)
            {
                for (long s = nextExpected; s < unwrapped; s++) pending.Add(s);   // skipped — lost or just late
                nextExpected = unwrapped + 1;
            }
            else if (pending.Remove(unwrapped))
            {
                reorders++;   // a previously-skipped number arrived late => reordering, not loss
            }
            // else: duplicate, or a late arrival already aged out — ignore

            // Anything that has fallen more than Window behind the frontier never came back: true loss.
            long cutoff = nextExpected - Window;
            while (pending.Count > 0 && pending.Min < cutoff)
            {
                pending.Remove(pending.Min);
                losses++;
            }
        }

        /// <summary>Returns (reorders, losses) accumulated since the last call and resets both to zero.</summary>
        public (int Reorders, int Losses) Exchange()
        {
            var result = (reorders, losses);
            reorders = 0;
            losses = 0;
            return result;
        }
    }
}
