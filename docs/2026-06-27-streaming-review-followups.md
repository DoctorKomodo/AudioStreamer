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

## 6. Drift cap did a full `ClearBuffer()` — audible dropout  **[DONE]**

When backlog exceeded `ReceiverMaxLatencyMilliseconds` the receiver dumped the
**entire** jitter buffer — a hard discontinuity (click + brief silence) every
time drift accumulated. Now it trims only the excess down to a frame-aligned
low-water mark (half the cap) by read-and-discarding from the buffer, so playback
stays continuous. (Adaptive resampling would be the textbook fix but is overkill
for a LAN tool.)

## 7. No network-loss visibility (sequence numbers)  **[DONE]**

The `overflows` counter measures the jitter buffer, not the wire. The header now
carries a 1-byte wrapping sequence number; the receiver counts forward gaps as
lost datagrams (backward jumps = reorder/dup, not counted) and reports `lost/s`
in diagnostics (`DiagnosticsSnapshot.LostPerSec`).

## 8. Dead initial `BufferedWaveProvider`  **[DONE]**

The throwaway `BufferedWaveProvider` at the top of `StartReceiver` was always
replaced in `InitializeReceiver` before playback. Removed (the field is now
`null!` until the first packet reveals the real format).

---

## 9. Format assumption: declared 16-bit vs device mix format  **[DONE]**

Investigation (dev machine: Arctis Nova Elite) showed the device mix format is
`IeeeFloat 32-bit 48kHz 2ch`, but the WASAPI **shared-mode engine already
converts** it to whatever PCM format the capture requests — forcing 16-bit PCM,
and even a non-native 44.1kHz, both succeed and capture correctly. So the app was
already converting to the *configured* format (not hardcoded 16-bit) via the OS;
the "receiver plays noise" risk only materialises if a device *rejects* the
requested format, in which case `StartRecording` throws (graceful start failure,
not noise). Changes made:
- **Log** the device-native vs wire format at sender start (`LogCaptureFormat`),
  so a rejected-format machine is diagnosable from the log.
- Add `AutoConvertPcm | SrcDefaultQuality` capture stream flags to make the
  shared-mode conversion explicit and cover older/edge devices.

No hand-rolled resampler needed — the OS does the conversion.

---

## 10. Underruns were invisible (shrinking-direction drift)  **[DONE]**

Field finding: a small audible drop every few minutes with **nothing in the
log**. Root cause: the drift cap is one-directional — it logs `resync/s` only
when backlog *grows* (sender clock faster). When the **receiver** clock is
faster, backlog *drains* to empty and `WasapiOut` plays a brief zero-filled
silence (the "drop"). That underrun was counted nowhere: `BufferedWaveProvider`
with ReadFully always returns the full count, so the receive loop never sees the
shortfall — only the render thread does. Backlog being sampled once/sec also hid
the sub-second dip to 0.

Fix: `UnderrunCountingWaveProvider` — a transparent pass-through wrapping the
`BufferedWaveProvider` that `WasapiOut` plays through; it increments an
`Interlocked` counter when a render `Read` finds `BufferedBytes < count`. Surfaced
as `underrun/s`, plus `min` backlog (lowest in the interval) as the lead-in
signal. No added latency/allocation — one comparison + an occasional atomic
increment per render callback. Verified: a deficit feed reports sustained
`underrun/s` with `min`≈0; a surplus feed reports 0 after the one expected
startup blip.

---

## 11. `lost/s` couldn't tell loss from reordering  **[DONE]**

Field finding: the receiver reported a steady `lost/s` of a few per second on a
*wired* link, with audible glitches worse than Wi-Fi. `iperf3` between the two
machines proved the network is clean — 0% UDP loss at the audio profile and at
50 Mbit/s, ~0.08 ms jitter, on a 2.5 GbE link. So the app's "loss" was not on the
wire.

Root cause of the false count: the old meter counted any forward jump in the
sequence number as loss, which **packet reordering** also trips (a single swap
double-counts), and reordering is plausible on a multi-queue 2.5 GbE NIC handling
the sender's *bursty* datagrams — something `iperf3`'s even pacing never exercises.
Reordering also causes real glitches because the receiver appends samples in
arrival order.

Fix: `SequenceLossTracker` unwraps the 1-byte sequence and classifies each gap as
a **late arrival** (`reorder/s`) or a number that never arrives within a 32-packet
window (`lost/s`). Verified: 7 unit cases (in-order, swap, loss, wraparound,
multi-reorder, aged-out-late-arrival, combined) plus a real-code end-to-end
(reordered stream → `reorder/s` only; lossy stream → `lost/s` only).

Field result: confirmed reordering — `reorder/s` ticked 1–2 per second with
`lost/s=0` and a healthy backlog. The reordering is inherent to audio I/O: WASAPI
delivers samples in periodic buffer-period bursts, the sender ships each burst
back-to-back with no pacing, and a multi-queue NIC reorders packets sent
microseconds apart (iperf's even pacing never triggers it).

## 12. Reorder buffer — play in sequence order  **[DONE]**

`ReorderBuffer` sits between the receive socket and the `BufferedWaveProvider`:
in-order datagrams pass straight through (no copy, no added latency); an early one
is held until its predecessor arrives; a genuinely missing one is skipped once
`ReorderWindowPackets` (8 ≈ 40 ms) newer datagrams pile up behind it, so playback
never stalls indefinitely. Only out-of-order traffic allocates/holds. Verified: 7
unit cases (in-order, adjacent swap, multi-reorder, true-loss skip, late-arrival
drop, wraparound in-order, swap across the wrap boundary) all emit correctly
ordered output, plus a real-code smoke test (audio flows, clean stop). Depth is a
constant for now — could become a Config/UI knob.

Field result: glitches gone (reorders still visible in the log but no longer
audible). See item 13 for the depth-scaling limitation found at 32-bit.

## 13. Reorder window is fixed-depth — too shallow for hi-res formats  **[TODO]**

Field finding (32-bit samples): `reorder/s` approached the window depth of 8,
meaning the buffer was on the verge of giving up on packets that were merely late.
Root cause: sender bursts — and therefore reorder depth measured in *packets* —
grow with the data rate (bit depth × channels × sample rate), so a fixed 8-packet
window is too shallow for 32-bit / high-channel / hi-res streams (16-bit/48k/stereo
is fine).

Planned fix: scale `ReorderBuffer`'s window with the negotiated format's byte rate
so the give-up *time* stays roughly constant (~40 ms) regardless of format —
e.g. `clamp(8 × AverageBytesPerSecond / 192000, 8, 64)` (8 at the 16-bit/48k/stereo
baseline, 16 at 32-bit/48k/stereo, capped at 64 so a true loss can't stall too
long). Optionally expose it as a Config/UI knob too.

---

## Wire protocol (current)

Each UDP datagram: **4-byte header + raw PCM**.
- bytes 0–2: wave format — `sampleRate/1000`, `bitDepth`, `channels` (`PackWaveFormat`).
- byte 3: wrapping sequence number (`SequenceLossTracker` → `lost/s` + `reorder/s`).
- byte 4+: audio payload, ≤ `MaxUdpAudioBytes` (1440), sliced on whole-frame boundaries.

Both ends must run the same version (true of the format header already).

---

## Status

Review items (1–9) plus the field-found underrun gap (10), loss/reorder
classifier (11), and reorder buffer (12) are implemented. **Open:** item 13 —
scale the reorder window with the format's byte rate (deferred). Remaining caveat:
validation has been socket-level and real-code component tests plus the user's own
two-machine runs — no automated live-audio test.
