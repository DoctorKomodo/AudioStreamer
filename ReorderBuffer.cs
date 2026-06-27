namespace AudioStreamer
{
    /// <summary>
    /// Restores in-order playback from a UDP stream that may arrive slightly out of order. Datagrams are keyed
    /// by their unwrapped 1-byte sequence number; in-order ones pass straight through (no copy, no added
    /// latency), an early one is held until its predecessor arrives, and a genuinely missing one is skipped once
    /// <see cref="maxDepth"/> later datagrams are waiting on it (true loss, or reordering deeper than the buffer)
    /// so playback never stalls indefinitely. Runs on the receive thread; allocates only when actually holding
    /// an out-of-order datagram.
    /// </summary>
    public sealed class ReorderBuffer
    {
        private readonly int maxDepth;
        private readonly Action<byte[], int, int> emit;   // (buffer, offset, count) — feeds the audio buffer in order
        private long nextSeq = -1;                         // unwrapped sequence we still owe the audio buffer
        private readonly SortedDictionary<long, byte[]> held = new();

        public ReorderBuffer(int maxDepth, Action<byte[], int, int> emit)
        {
            this.maxDepth = maxDepth;
            this.emit = emit;
        }

        public void Add(byte seq, byte[] buffer, int offset, int count)
        {
            long unwrapped;
            if (nextSeq < 0)
            {
                unwrapped = seq;        // first datagram defines the baseline
                nextSeq = unwrapped;
            }
            else
            {
                // Unwrap the byte to the continuous value nearest nextSeq (assumes jumps stay within ±128).
                int delta = (seq - (int)(nextSeq & 0xFF)) & 0xFF;
                unwrapped = delta < 128 ? nextSeq + delta : nextSeq - (256 - delta);
            }

            if (unwrapped < nextSeq)
                return;   // already emitted past this sequence — a too-late arrival; drop it

            if (unwrapped == nextSeq && held.Count == 0)
            {
                emit(buffer, offset, count);   // exactly in order, nothing waiting — straight through, no copy
                nextSeq++;
                return;
            }

            if (!held.ContainsKey(unwrapped))
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                held[unwrapped] = copy;
            }

            EmitContiguous();
            // Too many datagrams piled up behind a missing one: give up on it and skip ahead so audio keeps flowing.
            while (held.Count >= maxDepth)
            {
                nextSeq++;
                EmitContiguous();
            }
        }

        private void EmitContiguous()
        {
            while (held.TryGetValue(nextSeq, out var data))
            {
                held.Remove(nextSeq);
                emit(data, 0, data.Length);
                nextSeq++;
            }
        }
    }
}
