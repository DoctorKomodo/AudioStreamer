# Streaming Logic Review — Findings & Followups

Date: 2026-06-27
Scope: the sender (`StartSender`) and receiver (`StartReceiver`) hot paths in
[`AudioStreamerLogic.cs`](../AudioStreamerLogic.cs). Goal: low-latency, reliable
LAN audio streaming.

Status legend: **[DONE]** implemented · **[TODO]** not yet done · **[VERIFY]** needs confirmation.

---

## 1. UDP datagrams exceed MTU → IP fragmentation  **[DONE]**

The sender packed an entire `DataAvailable` buffer into one datagram
(`new byte[e.BytesRecorded + 3]` → `client.Send(...)`). At 48 kHz/16-bit/stereo
(~192 KB/s) each callback carries ~10 ms (~1.9 KB) up to the requested ~100 ms
buffer (~19 KB). Anything over ~1472 bytes doesn't fit a 1500-byte Ethernet MTU,
so IP fragments it — and **if any one fragment is lost the kernel discards the
whole datagram**, turning small loss into large audio gaps. Large datagrams also
raise latency (transmit in lumps).

Fix: chunk each captured buffer into MTU-sized slices (≤~1440 bytes of audio +
the 3-byte header per slice), one datagram per slice. Eliminates fragmentation,
makes loss granular, smooths latency.

## 2. Per-packet heap allocations on the audio threads  **[DONE]**

- Sender allocated a new `byte[]` per callback.
- Receiver: `UdpClient.Receive` allocates a fresh `byte[]` per datagram.

These run on the capture/receive threads; the Gen0 garbage invites GC pauses,
which on the audio path cause dropouts. Fix: reuse a preallocated send buffer
(grow only when needed) and switch the receiver to `Socket.ReceiveFrom` with a
reusable buffer.

## 3. Windows UDP connection-reset (WSAECONNRESET / 10054)  **[DONE]**

On Windows a UDP socket can throw `SocketException` 10054 on `Receive` when a
prior send drew an ICMP "port unreachable" (e.g. sender starts before receiver
binds). The loop caught and retried so it survived, but it spammed the log and
could spin. Fix: disable once at socket creation via the `SIO_UDP_CONNRESET`
ioctl.

## 4. Socket buffer sizing  **[DONE]**

Default UDP buffers (~64 KB) can overflow under burst/jitter and the kernel
silently drops. Bump `ReceiveBufferSize` / `SendBufferSize` to a few hundred KB
to absorb jitter cheaply.

## 5. Sender `Connect()` once instead of per-send endpoint  **[DONE]**

`client.Send(buf, len, endPoint)` re-evaluates the remote each call. Calling
`Connect(endPoint)` once and `Send(buf, len)` thereafter trims minor per-call
overhead. (Folded into the sender rework.)

---

## 6. Drift cap does a full `ClearBuffer()` — audible dropout  **[TODO]**

When backlog exceeds `ReceiverMaxLatencyMilliseconds`, the receiver dumps the
**entire** jitter buffer — a hard discontinuity (click + brief silence) every
time drift accumulates. Smoother: trim only the excess down to a low-water mark
(read-and-discard N bytes to reach, say, half the cap) instead of emptying
everything. A true fix is adaptive resampling, but that's a much larger change
and overkill for a LAN tool.

## 7. No network-loss visibility (sequence numbers)  **[TODO]**

The `overflows` counter measures the jitter buffer, not the wire. A 1-byte
sequence number per datagram would let the receiver count actually-dropped or
reordered packets, turning diagnostics into a real loss meter. Cheap and
informative; also enables detecting reordering (more likely once datagrams are
small and numerous).

## 8. Dead initial `BufferedWaveProvider`  **[TODO / cleanup]**

The `BufferedWaveProvider` built at the top of `StartReceiver` is always
replaced in `InitializeReceiver` before playback starts. Can be removed for
clarity.

---

## 9. Format assumption: declared 16-bit vs device mix format  **[VERIFY]**

WASAPI loopback in shared mode captures at the **device mix format**, commonly
32-bit float, yet the capture is configured as `WaveFormat(SampleRate, 16,
Channels)` and the header advertises 16-bit. If a machine's mix format isn't
16-bit PCM, the receiver would interpret float bytes as PCM (loud noise). Works
on the current dev machine (its device is presumably 16-bit), but it's fragile
across machines. Robust fix: read the actual `capture.WaveFormat` after init and
put *that* in the header rather than the config values.

---

## Implementation order (from the review)

1. **MTU chunking + buffer reuse** (items 1, 2, 5) — biggest reliability + latency win.  ← this branch
2. **`SIO_UDP_CONNRESET` ioctl** (item 3).  ← this branch
3. **Socket-buffer sizing** (item 4).  ← this branch
4. Partial-trim drift cap (item 6).  ← follow-up
5. Sequence numbers / loss meter (item 7), dead-object cleanup (item 8), format hardening (item 9).  ← follow-up
