# Optional Opus Compression (Alternative to Raw PCM) — Design

Date: 2026-06-28
Status: design (exploratory — not scheduled; "might do later")

## Goal

Add **Opus** as an optional, low-latency, high-quality compressed alternative to the current
raw-PCM wire payload, selectable per session and **defaulting to PCM** (zero behaviour change
when off). The codec choice is negotiated implicitly through the packet header — the receiver
adapts to whatever the sender announces — so no new UI is strictly required on the receiver.

This is a curiosity/enhancement, not a fix. On a LAN, raw PCM already works fine (48 kHz/16-bit
stereo ≈ 1.5 Mbps); Opus buys lower bandwidth, built-in packet-loss concealment (PLC) and
optional in-band FEC, at the cost of a managed codec dependency and a format-subset restriction.

## Why Opus (vs. the alternatives)

- **Opus** — purpose-built for real-time interactive audio (RFC 6716). Frame sizes 2.5–60 ms;
  `OPUS_APPLICATION_RESTRICTED_LOWDELAY` (CELT-only) cuts algorithmic delay from ~26.5 ms to
  ~5 ms. Royalty-free, no patent-licensing burden. Has PLC + optional FEC, which dovetails with
  the existing `ReorderBuffer`/`SequenceLossTracker` loss machinery. **Chosen.**
- **AAC-LD/ELD** — comparable low delay but patent-encumbered and awkward in managed .NET.
- **FLAC** — lossless and handles 24-bit, but block sizes (~85 ms at 48 kHz) make it unsuited
  to the sub-10 ms interactive target. (Noted as the choice if bit-exact hi-res ever matters.)

## Library

**Concentus** — a pure-C# Opus implementation (no native DLL, no P/Invoke), so it fits the
framework-dependent, single-project, managed deployment with no installer changes.

- **Pin Concentus 2.x.** The 1.x API exposes concrete `OpusEncoder`/`OpusDecoder`; 2.x moved to
  `IOpusEncoder`/`IOpusDecoder` via `OpusCodecFactory.Create*` and added `Span<T>` overloads.
- **Use the `Span<T>` encode/decode overloads on the audio thread.** They avoid the
  `short[]`↔`byte[]` intermediate copies, preserving the existing no-alloc-on-the-capture/
  receive-thread discipline (a GC pause on those threads is an audible dropout).
- Added via `<PackageReference>` in the csproj. This is the only new dependency.

## Locked decisions

- **PCM stays the default.** Opus is opt-in via a new `Config.Codec` field; an unset/old
  `config.json` ⇒ PCM ⇒ byte-for-byte the current behaviour and wire layout aside from the new
  codec header byte (see compatibility note).
- **Codec is announced on the wire, per datagram**, as a new 1-byte header field — the receiver
  adapts; it is not separately configured. (Receiver mode already derives everything from the
  header.)
- **Opus frame = 10 ms, RESTRICTED_LOWDELAY, bitrate 192 kbps.** 10 ms (480 samples/ch @ 48 kHz)
  balances delay vs. per-packet overhead; 192 kbps is generous because it's a LAN. All three are
  constants to start (could become advanced config later — out of scope here).
- **Opus path is gated to the format subset Opus supports**: 16-bit, ≤ 2 channels, sample rate ∈
  {8000, 12000, 16000, 24000, 48000}. The catalog's other rates (44100, 88200, 96000, 176400,
  192000), 24/32-bit depths, and 6/8-channel formats **fall back to PCM automatically** (with a
  one-line log). Rationale: Opus ingests int16/float at Opus rates only; converting hi-res/
  multichannel is out of scope and partly defeats the point.

## Architecture / components

### 1. `Streaming/WireProtocol.cs` — add a codec header byte

Insert a codec id between the format code and the sequence number. New datagram layout:

| offset | bytes | meaning |
|--------|-------|---------|
| 0 | 1 | format code (index into `AudioFormats.Formats`) — the PCM format the payload decodes **to** |
| 1 | 1 | codec id (`CodecId`: `Pcm=0`, `Opus=1`) |
| 2 | 1 | wrapping per-datagram sequence number |
| 3+ | … | payload: raw PCM **or** one Opus frame |

Constant changes: `CodecByteOffset` = **1** (new), `SequenceByteOffset` 1 → **2**,
`HeaderBytes` 2 → **3**. `FormatHeaderBytes` (1) and `MaxUdpAudioBytes` (1440) unchanged. The PCM
datagram grows by one byte (1443 + 28 IP/UDP = 1471 ≤ 1500 — still no fragmentation). Opus frames
are a few hundred bytes, far under the budget.

New thin accessors (WireProtocol stays pure framing — it knows neither the catalog nor Opus):

```csharp
public enum CodecId : byte { Pcm = 0, Opus = 1 }
public const int CodecByteOffset = 1;

public static void WriteCodec(byte[] dest, CodecId c) => dest[CodecByteOffset] = (byte)c;
public static CodecId ReadCodec(byte[] src) => (CodecId)src[CodecByteOffset];
```

The format code is **retained even for Opus**: it tells the receiver which WASAPI output
(rate/channels) to build — Opus decodes *to* that PCM format. Both sides already reference the
named offset constants, so the shift flows through with minimal edits.

### 2. `Core/AudioStreamerLogic.cs` — `Config.Codec`

Add `public CodecId Codec { get; set; } = CodecId.Pcm;` to the nested `Config`. Persists with the
existing `System.Text.Json` integer-enum convention (byte value `0`/`1` in `config.json`). Per the
`LoadConfig()` note in CLAUDE.md, an existing valid config lacking the field keeps the in-memory
default (`Pcm`) until next save — which is exactly the desired backward-compatible behaviour.

### 3. Codec seam — `Streaming/IAudioCodec.cs` (new)

The PCM and Opus paths differ in **where** framing happens: PCM slices by MTU (whole frames),
Opus by fixed sample-count frames. The clean seam is "PCM in → datagram(s) out" on send and
"datagram → PCM out (+ PLC on a gap)" on receive:

```csharp
internal interface IAudioEncoder : IDisposable
{
    // Append captured PCM; invoke `emit(payload, length)` once per complete output frame
    // (zero or more per call). PCM impl: today's MTU slicing. Opus impl: accumulate to a
    // 10 ms frame, then Encode.
    void Push(byte[] pcm, int offset, int count, Action<byte[], int> emit);
}

internal interface IAudioDecoder : IDisposable
{
    int Decode(byte[] packet, int offset, int count, byte[] pcmOut);  // payload -> PCM bytes
    int Conceal(byte[] pcmOut);                                       // PLC for a dropped frame
}
```

Two trivial impls (`PcmCodec` = identity slice / memcpy, preserving current behaviour exactly)
and two Opus impls (`OpusEncoderAdapter`/`OpusDecoderAdapter` wrapping Concentus). A small factory
maps `(CodecId, Format)` → encoder/decoder, applying the **subset gate**: if the requested format
is outside Opus's support, the factory returns the PCM codec and logs the fallback once.

### 4. Sender — `Streaming/SenderSession.cs`

In `StartCapture`, after computing the format code, resolve the codec via the factory (with
fallback), write the codec byte into the reused `sendBuffer` once (constant per session, like the
format code), and route `DataAvailable` PCM through the encoder:

```csharp
WireProtocol.WriteFormatCode(sendBuffer, formatCode);
WireProtocol.WriteCodec(sendBuffer, effectiveCodec);   // Pcm or Opus after subset gate
var encoder = CodecFactory.CreateEncoder(effectiveCodec, format, logLine);
// DataAvailable:
encoder.Push(e.Buffer, 0, e.BytesRecorded, (payload, len) =>
{
    var socket = udpClient; if (socket == null) return;
    sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;
    Buffer.BlockCopy(payload, 0, sendBuffer, WireProtocol.HeaderBytes, len);
    socket.Send(sendBuffer, len + WireProtocol.HeaderBytes);
    // sentPackets/sentBytes accounting unchanged
});
```

The **Opus encoder adapter** holds a frame accumulator: WASAPI hands arbitrary buffer sizes, but
Opus needs exactly a frame. It fills a `short[samplesPerFrame * channels]`, and each time the
accumulator is full calls `Encode` (RESTRICTED_LOWDELAY) and emits one datagram, retaining the
partial remainder for the next `Push`. The encoder, accumulator, and scratch output buffer are all
allocated once per (re)build — never per callback — so the capture thread stays alloc-free. The
existing auto-recovery (`RestartCapture`) rebuilds the encoder alongside the capture with no
special handling.

The **PCM encoder adapter** is exactly today's MTU slice loop, moved behind `Push` — so the PCM
path is mechanically identical to current behaviour.

### 5. Receiver — `Streaming/ReceiverSession.cs`

`InitializeReceiver` reads the codec byte alongside the format code, validates both (unknown
codec ⇒ logged-once-and-skipped, exactly like the existing unknown-format-code guard, CLAUDE.md
item 16), and builds the matching decoder. The `BufferedWaveProvider`/`UnderrunCountingWaveProvider`/
`WasapiOut`/latency-cap pipeline is **unchanged** — it always sees plain PCM.

`ReceiveAudio` decodes inside the `ReorderBuffer` flush callback instead of memcpy'ing. The reorder
buffer now reorders the **compressed** datagrams (still keyed by the sequence byte) and the
callback decodes each in order:

```csharp
var reorderBuffer = new ReorderBuffer(reorderWindow,
    onRelease: (buf, off, cnt) =>
    {
        int bytes = decoder.Decode(buf, off, cnt, pcmScratch);
        if (bufferedWaveProvider.BufferedBytes + bytes > bufferedWaveProvider.BufferLength) overflows++;
        bufferedWaveProvider.AddSamples(pcmScratch, 0, bytes);
    },
    onLost: () =>                              // NEW second delegate — see ReorderBuffer change
    {
        int bytes = decoder.Conceal(pcmScratch);   // Opus PLC; PCM impl emits nothing (bytes=0)
        bufferedWaveProvider.AddSamples(pcmScratch, 0, bytes);
    });
```

### 6. `Streaming/ReorderBuffer.cs` — additive `onLost` hook

Today, when the window fills and a sequence number is genuinely missing, the buffer **silently
skips** it. Add an optional second `onLost` delegate fired at that skip point so the Opus decoder
can emit a concealment frame (Opus PLC interpolates a plausible ~10 ms rather than leaving a hole).
This is **additive** — `onLost` defaults to a no-op, so the PCM path (and every existing call site)
is unchanged. The PCM decoder's `Conceal` returns 0 bytes, preserving today's "skip the gap"
behaviour byte-for-byte.

**Optional upgrade (in scope to consider, off by default):** pass `decode_fec: true` so Opus
reconstructs a lost frame from redundancy carried in the *next* packet — a strict improvement over
"drop whole frames," at a small bitrate cost. Defer unless trivial.

### 7. UI — `UI/MainWindow.xaml` + `.cs` (minimal)

Add a **Codec** selector to `SenderPanel` (a 2-item `ComboBox`: PCM / Opus, same 200-width
layout, value-backed like the format dropdowns). `UpdateConfigFromUI`/`PopulateUIFromConfig` read/
write `Config.Codec` via `SelectedValue`/`SelectValue`, mirroring the existing format-dropdown
plumbing. The receiver needs no UI (it adapts to the header). The selector lives in `SenderPanel`,
so it's hidden in Receiver mode by the existing `UpdateModePanels` logic.

### 8. Diagnostics (small, optional)

The compact diagnostics line could surface the active codec and (receiver) PLC-frame count. The
existing `lost/s`/`reorder/s` meters already cover the wire; `underrun/s` still reflects drain-side
clock drift. Minimal change: prepend `codec=opus` to the status/diagnostics line. Adding a
dedicated `pl c/s` counter is optional polish (would extend `DiagnosticsSnapshot`).

## Data flow

```
Sender: PCM capture → encoder.Push
  PCM:  MTU slice                → datagram[3+] = PCM frame
  Opus: accumulate 10ms → Encode → datagram[3+] = Opus frame
  datagram[0]=format code, [1]=codec id, [2]=sequence

Receiver: datagram[1] → ReadCodec → build PcmCodec | OpusDecoder (once)
  in-order release → decoder.Decode → PCM → BufferedWaveProvider
  window-fill gap  → decoder.Conceal → PLC PCM (Opus) | nothing (PCM)
  → UnderrunCountingWaveProvider → WasapiOut   (pipeline unchanged)
```

## Error handling / edge cases

- **Old/hand-edited config without `Codec`:** defaults to `Pcm` (LoadConfig keeps the in-memory
  default). No breakage.
- **Sender selects Opus with an unsupported format** (e.g. 96 kHz/24-bit/6ch): the factory's
  subset gate silently falls back to PCM and logs once. The wire then carries `CodecId.Pcm`, so the
  receiver does the right thing with no coordination.
- **Receiver sees an unknown codec id** (foreign datagram / version mismatch): logged once and the
  datagram is ignored — identical pattern to the existing unknown-format-code guard.
- **Capture auto-recovery (RestartCapture):** the encoder is rebuilt with the capture; the Opus
  accumulator restarts empty (a one-frame discontinuity at resume, same class as the existing
  sequence-restart blip — acceptable).
- **Output auto-recovery (RestartOutput):** untouched — the decoder lives in the receive loop, not
  the output path; `BuildAndPlayOutput` still flushes to the live edge.
- **Latency cap / drift control:** unchanged — it operates on decoded PCM backlog. Note Opus's
  ~5 ms algorithmic delay adds a small *fixed* latency on top of the buffer backlog; it does not
  interact with the drift cap.
- **CPU:** Concentus is managed and slower than native libopus, but a single 48 kHz stereo stream
  at 10 ms frames is well within budget on a desktop. The process already runs `AboveNormal`.

## Backward / cross-version compatibility

**Breaking wire change** (new codec byte; sequence offset shifts 1 → 2). Old and new builds cannot
interoperate — acceptable under the existing "both sides must run the same version" rule (CLAUDE.md,
`WireProtocol`; the catalog order is already version-locked). `config.json` is **not** broken: the
schema only gains an optional `Codec` field that defaults safely.

If a non-breaking rollout were ever required (not a goal here), the codec byte could instead steal
the high bit of an otherwise-unused header position — rejected as hacky; a clean explicit byte is
worth the version bump given the same-version rule already holds.

## Out of scope

- Per-session bitrate / frame-size / FEC UI (constants for now).
- 24/32-bit, >2-channel, or non-Opus-rate compression (those formats stay PCM).
- A second lossless codec (FLAC) — noted as the answer if bit-exact hi-res is ever wanted.
- Native libopus / P-Invoke (Concentus keeps deployment managed and installer-unchanged).
- Receiver-side codec selection UI (receiver adapts to the header by design).

## Verification

No test project exists. Plan-time verification:
- Build clean (0/0) with the Concentus package restored.
- **PCM regression (must be byte-identical behaviour):** with `Codec = Pcm`, run a Sender↔Receiver
  loop; confirm audio, diagnostics, latency cap, and both auto-recovery paths behave exactly as
  today. (The codec byte is the only wire difference.)
- **Opus happy path:** select Opus at 48 kHz/16-bit/stereo; confirm the receiver logs `codec=opus`,
  audio plays at correct pitch, and `KB/s` drops markedly vs. PCM.
- **Subset fallback:** select Opus at, e.g., 96 kHz/24-bit/6ch; confirm the sender logs the PCM
  fallback and the receiver plays PCM.
- **Loss concealment:** induce loss (or rely on natural Wi-Fi loss); confirm `lost/s` ticks while
  audio degrades gracefully (PLC) rather than clicking, and — if FEC is enabled — that loss is
  largely inaudible.
- A throwaway console check (optional, no framework): round-trip a known PCM frame through
  `OpusEncoderAdapter`→`OpusDecoderAdapter` and assert sample count / rough RMS similarity, and that
  `PcmCodec` is a bit-exact identity.

## Docs to update (part of the work)

- `CLAUDE.md`: the wire-protocol paragraph (new codec byte + offsets), the Sender/Receiver
  paragraphs (encoder accumulator / decoder + PLC), the `Config` list (`Codec` field), and the
  MainWindow UI paragraph (Codec dropdown).
- `docs/2026-06-27-streaming-review-followups.md`: add the new "Wire protocol (current)" layout
  (3-byte header) if/when this lands.
