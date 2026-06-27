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

## Wire protocol (current)

Each UDP datagram: **4-byte header + raw PCM**.
- bytes 0–2: wave format — `sampleRate/1000`, `bitDepth`, `channels` (`PackWaveFormat`).
- byte 3: wrapping sequence number (loss meter).
- byte 4+: audio payload, ≤ `MaxUdpAudioBytes` (1440), sliced on whole-frame boundaries.

Both ends must run the same version (true of the format header already).

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

## Wire protocol (current)

Each UDP datagram: **4-byte header + raw PCM**.
- bytes 0–2: wave format — `sampleRate/1000`, `bitDepth`, `channels` (`PackWaveFormat`).
- byte 3: wrapping sequence number (loss meter).
- byte 4+: audio payload, ≤ `MaxUdpAudioBytes` (1440), sliced on whole-frame boundaries.

Both ends must run the same version (true of the format header already).

---

## Status

All review items (1–9) plus the field-found underrun gap (10) are implemented.
Remaining caveat: validation has been socket-level and real-code component tests
plus the user's own two-machine runs — no automated live-audio test.
