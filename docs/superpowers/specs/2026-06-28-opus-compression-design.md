# Optional Opus Compression (Alternative to Raw PCM) — Design

Date: 2026-06-28 (rev. 2026-06-30)
Status: design (exploratory — not scheduled; "might do later")

## Goal

Add **Opus** as an optional, low-latency, lossy compressed alternative to the current raw-PCM wire
payload, selectable per session and **defaulting to PCM** (zero behaviour change when off). The codec
choice is announced in the packet header — the receiver adapts to whatever the sender sends — so no
new UI is required on the receiver.

**Primary use case: Wi-Fi.** On a wired LAN, raw PCM already works fine (48 kHz/16-bit stereo ≈
1.5 Mbps) and there is little reason to compress. The case for Opus is **Wi-Fi**, where bandwidth can
be tight/contended and packet loss is real: Opus cuts the stream to ~10–15 % of PCM and brings
built-in packet-loss concealment (PLC), which dovetails with the existing
`ReorderBuffer`/`SequenceLossTracker` loss machinery. The cost is honest: Opus is **lossy** (not
bit-exact like today's PCM), it adds a native codec dependency (libopus via OpusSharp — see Library),
and it adds a few ms of latency (quantified below).

> A separate exploratory investigation of *lossless* Wi-Fi loss resilience (parity FEC / retransmission
> over PCM) is recorded in
> [Lossless Wi-Fi Loss Resilience](2026-06-30-lossless-resilience-alternatives-design.md); it does not
> drive this spec.

## Why Opus (vs. the alternatives)

- **Opus** — purpose-built for real-time interactive audio (RFC 6716).
  `OPUS_APPLICATION_RESTRICTED_LOWDELAY` (CELT-only) drops the SILK layer to minimise algorithmic
  delay. Royalty-free, no patent-licensing burden. Decoder-side PLC works in all modes. **Chosen.**
- **AAC-LD/ELD** — comparable low delay but patent-encumbered and awkward in managed .NET.
- **FLAC** — lossless and handles 24-bit, but block sizes (~85 ms at 48 kHz) make it unsuited to the
  interactive target. (Noted as the choice if bit-exact hi-res ever matters.)

## Library

**OpusSharp** — a native P/Invoke binding to the official **libopus 1.6.1**, shipping prebuilt native
binaries via NuGet. **Chosen** over the managed C# port Concentus because it allocates **nothing on the
audio thread** (see benchmark under Verification), where a GC pause would be an audible dropout.

- **Deployment is a non-issue.** The win-x64 `opus.dll` loads automatically from the NuGet native
  assets; AudioStreamer is Windows-x64-only with an installer that already copies the publish output.
  Reference `OpusSharp.Core` + the win-x64 native only, to avoid shipping the other platforms' libs.
- **Configuration** uses the `OpusSharp.Core.Extensions.OpusEncoderExtensions` setters (`SetBitRate`,
  `SetComplexity`, `SetVbr`), not the raw `Ctl` overload; PLC is a decode of a null/empty packet. (Exact
  working calls are in the OpusBench harness — one-off API gotchas belong in the implementation plan.)
- **Fallback:** Concentus 2.2.2 is the managed-only alternative if a zero-native-binary deployment ever
  became a hard requirement — at the cost of its per-call allocation (see benchmark).
- Added via `<PackageReference>`; the only new dependency.

## Decisions

- **PCM stays the default.** Opus is opt-in via a new `Config.Codec` field; an unset/old `config.json`
  ⇒ PCM ⇒ byte-for-byte the current behaviour and wire layout aside from the new codec header byte (see
  compatibility note).
- **Codec is announced on the wire, per datagram**, as a new 1-byte header field — the receiver adapts;
  it is not separately configured. (Receiver mode already derives everything from the header.)
- **Selecting Opus fixes the session to Opus-optimal parameters:** 48 kHz, 16-bit, stereo. The sender
  already captures through the WASAPI shared-mode engine with `AutoConvertPcm | SrcDefaultQuality`
  (`SenderSession.TweakedWasapiLoopbackCapture.GetAudioClientStreamFlags`), which resamples/downmixes
  the device's native mix format to whatever PCM format we request — so forcing 48 kHz/16-bit/stereo
  costs nothing extra and needs no custom resampler. Consequences:
  - No per-format gate and no fallback: any source rate/depth/channel count is converted at capture, so
    Opus always means 48 kHz/16-bit/stereo (44.1 kHz, hi-res, and multichannel sources all just work).
  - This is an **effective capture format for the Opus session, computed locally** — it does **not**
    overwrite the user's stored `SampleRate`/`Bits`/`Channels`, which stay intact for the PCM path. The
    UI simply disables those dropdowns while Opus is selected.
  - The wire format code and codec id are both constant for an Opus session, so neither changes
    mid-stream and there is no mid-stream-codec-change hazard.
- **Application mode: `RESTRICTED_LOWDELAY` (CELT-only)** — minimum latency. `OPUS_APPLICATION_AUDIO`
  costs +4 ms of lookahead (6.5 vs 2.5 ms) for no usable benefit here: its only extra is SILK-layer
  features (in-band FEC / DTX) that never engage for fullband music (see Loss handling).
- **Bitrate: 192 kbps VBR.** Effectively transparent for stereo music with headroom, and still ~10 % of
  PCM (a large Wi-Fi win). VBR is Opus's default and gives the best quality/bit; the variable packet
  size is fine for our per-frame UDP. A default, not a hard constraint — 128–160 kbps would also be
  transparent if leaner Wi-Fi use ever mattered.
- **Frame size: a user setting, default 10 ms.** `Config.OpusFrameSamples`, with a Sender-panel
  dropdown; options **2.5 / 5 / 10 / 20 ms** (120 / 240 / 480 / 960 samples/ch @ 48 kHz). Tradeoff
  (measured — see Verification): total codec delay = frame + 2.5 ms lookahead (**5–22.5 ms** across the
  options); packet rate is inverse (10 ms → 100/s, 5 ms → 200/s, 2.5 ms → 400/s); the real cost of small
  frames is **IP/UDP header overhead** (~+100 kbps at 2.5 ms, negligible at 20 ms), not Opus coding
  efficiency (~1.5 %) or CPU (<1 % of a core). 40/60 ms are omitted as too high-latency. Frame size is a
  **Sender-only** choice — Opus packets are self-describing, so it needs no wire signalling and no
  receiver config.
- **Latency, stated honestly.** CELT algorithmic delay = frame + lookahead; the RESTRICTED_LOWDELAY
  lookahead is **2.5 ms** (measured). At the 10 ms default that is **~12.5 ms** of codec delay, plus up
  to one frame (≤10 ms) of accumulator buffering — so **~12–22 ms** over today's PCM path at the
  default, scaling with the chosen frame size. Well inside the receiver's 150 ms drift cap, but a real,
  fixed addition, owned here rather than hidden.

## Architecture / components

### 1. `Streaming/WireProtocol.cs` — add a codec header byte

Insert a codec id between the format code and the sequence number. New datagram layout:

| offset | bytes | meaning |
|--------|-------|---------|
| 0 | 1 | format code (index into `AudioFormats.Formats`) — the PCM format the payload decodes **to** |
| 1 | 1 | codec id (`CodecId`: `Pcm=0`, `Opus=1`) |
| 2 | 1 | wrapping per-datagram sequence number |
| 3+ | … | payload: raw PCM **or** one Opus frame |

Constant changes: `CodecByteOffset` = **1** (new), `SequenceByteOffset` 1 → **2**, `HeaderBytes`
2 → **3**. `FormatHeaderBytes` (1) and `MaxUdpAudioBytes` (1440) unchanged. The PCM datagram grows by
one byte (1443 + 28 IP/UDP = 1471 ≤ 1500 — still no fragmentation). Opus frames are a few hundred
bytes, far under the budget.

`CodecId` is a **public top-level enum** (not nested in the `internal WireProtocol`) because the
persisted `public Config.Codec` property exposes it — a public property cannot expose an internal type
(CS0053). The accessors stay in `WireProtocol` (pure framing — it knows neither the catalog nor Opus):

```csharp
public enum CodecId : byte { Pcm = 0, Opus = 1 }   // public, top-level (declared alongside WireProtocol)

// on WireProtocol:
public const int CodecByteOffset = 1;
public static void WriteCodec(byte[] dest, CodecId c) => dest[CodecByteOffset] = (byte)c;
public static CodecId ReadCodec(byte[] src) => (CodecId)src[CodecByteOffset];
```

The format code is **retained even for Opus**: it tells the receiver which WASAPI output
(rate/channels) to build — Opus decodes *to* that PCM format (always 48 kHz/16-bit/stereo here). Both
sides already reference the named offset constants, so the shift flows through with minimal edits.

### 2. `Core/AudioStreamerLogic.cs` — `Config.Codec` + `Config.OpusFrameSamples`

Add to the nested `Config`:
- `public CodecId Codec { get; set; } = CodecId.Pcm;`
- `public int OpusFrameSamples { get; set; } = 480;` — Opus frame size in **samples per channel** at
  48 kHz (480 = 10 ms). Valid: 120/240/480/960 (2.5/5/10/20 ms). Stored as samples (not ms) because
  that is exactly libopus's `frame_size` argument and it avoids a fractional-ms field for 2.5 ms.

`CodecId` is the `WireProtocol` enum (§1); `System.Text.Json` persists it as its integer value, exactly
like the existing `Config.Mode` enum. Note the format dropdowns' `SelectValue`/`SelectedValue` helpers
are **`int`-based** (`FormatOption.Value` is `int`), so the Codec selector uses int values `0`/`1` and
`UpdateConfigFromUI`/`PopulateUIFromConfig` cast to/from `CodecId` at that seam.

Both persist with the existing `System.Text.Json` conventions; per the `LoadConfig()` note in
CLAUDE.md, an existing valid config lacking either field keeps the in-memory default (`Pcm` / `480`)
until next save — backward-compatible. `OpusFrameSamples` only affects the Opus path; on load the sender
clamps an out-of-range value to the nearest valid option (defensive against a hand-edited config).

When `Codec == Opus`, the sender derives an **effective capture format** of 48 kHz/16-bit/stereo for
capture and the wire format code, **without modifying** the stored `SampleRate`/`Bits`/`Channels` (so
switching back to PCM restores the user's format). The UI disables those dropdowns while Opus is
selected. `OpusFrameSamples` is Sender-only (the receiver reads frame duration from each packet).

### 3. Codec seam — `Streaming/IAudioCodec.cs` (new)

The PCM and Opus paths differ in **where** framing happens: PCM slices by MTU (whole frames), Opus by
fixed sample-count frames. The clean seam is "PCM in → datagram(s) out" on send and "datagram → PCM
out (+ PLC on a gap)" on receive:

```csharp
internal interface IAudioEncoder : IDisposable
{
    // Append captured PCM; invoke `emit(payload, offset, length)` once per complete output frame
    // (zero or more per call). The offset lets PCM emit straight from the capture buffer with no copy;
    // Opus passes 0. PCM impl: today's MTU slicing. Opus impl: accumulate to one frame, then Encode.
    void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit);
}

internal interface IAudioDecoder : IDisposable
{
    int Decode(byte[] packet, int offset, int count, byte[] pcmOut);  // payload -> PCM bytes
    int Conceal(byte[] pcmOut);                                       // PLC for a dropped frame
}
```

Two trivial impls (`PcmCodec` = identity slice / memcpy, preserving current behaviour exactly) and
two Opus impls (`OpusEncoderAdapter`/`OpusDecoderAdapter` wrapping OpusSharp). A small factory maps
`CodecId` → encoder/decoder. There is no subset gate: Opus is only ever asked to handle
48 kHz/16-bit/stereo because the sender fixes that format whenever Opus is selected.

### 4. Sender — `Streaming/SenderSession.cs`

In `StartCapture`, after resolving the effective format and computing the format code, create the
codec, write the codec byte into the reused `sendBuffer` once (constant per session, like the format
code), and route `DataAvailable` PCM through the encoder:

```csharp
WireProtocol.WriteFormatCode(sendBuffer, formatCode);
WireProtocol.WriteCodec(sendBuffer, config.Codec);     // Pcm or Opus
var encoder = CodecFactory.CreateEncoder(config.Codec, format, frameSamples, logLine);
// DataAvailable:
encoder.Push(e.Buffer, 0, e.BytesRecorded, (payload, off, len) =>
{
    var socket = udpClient; if (socket == null) return;
    sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;
    Buffer.BlockCopy(payload, off, sendBuffer, WireProtocol.HeaderBytes, len);
    socket.Send(sendBuffer, len + WireProtocol.HeaderBytes);
    // sentPackets/sentBytes accounting unchanged
});
```

The **Opus encoder adapter** holds a frame accumulator: WASAPI hands arbitrary buffer sizes, but Opus
needs exactly a frame. It fills a `short[OpusFrameSamples * channels]`, and when full calls `Encode` and
emits one datagram, retaining the partial remainder for the next `Push`. The encoder, accumulator, and
scratch output buffer are allocated once per (re)build — never per callback — so the capture thread
stays alloc-free (OpusSharp adds no per-call allocation of its own). The encoder is created and stored
**inside the `lock (senderLock)` block** that owns `sendBuffer`/`waveSource` (the block `RestartCapture`
re-enters), so a rebuild swaps encoder and capture atomically. Because Opus forces stereo, a device flip
that changes native channel count does **not** change the wire format — the session stays
48 kHz/16-bit/stereo Opus throughout.

The **PCM encoder adapter** is exactly today's MTU slice loop, moved behind `Push` — so the PCM path is
mechanically identical to current behaviour.

### 5. Receiver — `Streaming/ReceiverSession.cs`

`InitializeReceiver` reads the codec byte alongside the format code, validates both (unknown codec ⇒
logged-once-and-skipped, exactly like the existing unknown-format-code guard, CLAUDE.md item 16), and
builds the matching decoder. The `BufferedWaveProvider`/`UnderrunCountingWaveProvider`/`WasapiOut`/
latency-cap pipeline is **unchanged** — it always sees plain PCM.

**Frame size needs no receiver config.** The sender's `OpusFrameSamples` is *not* signalled — Opus
packets are self-describing, so `Decode` recovers whatever frame duration the packet carries. The
receiver only sizes its decode scratch (`pcmScratch`) for the **largest supported frame** (20 ms =
960 samples/ch → 3840 bytes stereo; size generously). A sender frame-size change just works, no
coordination.

**Mid-stream codec change:** for robustness the codec byte is re-checked on the **same per-datagram
path** the receiver already uses to track the format code (rebuilding the decoder if it changes). In
practice an Opus session's format code and codec id are constant, so this never actually flips
mid-stream — but mirroring the existing format-tracking guard keeps the receiver uniform at negligible
cost (one byte-compare per datagram).

`ReceiveAudio` decodes inside the `ReorderBuffer` release callback instead of memcpy'ing. The reorder
buffer now reorders the **compressed** datagrams (still keyed by the sequence byte) and the callback
decodes each in order — correct because Opus decode is stateful and is fed strictly in sequence, with
`Conceal` filling any gap so decoder state stays continuous. A late datagram that arrives *after* its
gap has already been concealed is dropped (the existing `ReorderBuffer` already discards too-late
arrivals — decoder state has moved on, so it must not be decoded out of order). Decode/Conceal write
into a **new** `pcmScratch` buffer (sized for the largest supported frame — see above); sharing one
buffer is safe because the receive loop is single-threaded. Both paths route their PCM through the
existing `EmitSamples` helper (today's overflow-check + `AddSamples`) so the buffered-provider field is
read live and survives a pipeline rebuild:

```csharp
// pcmScratch: NEW buffer, largest-frame-sized.  EmitSamples: existing overflow-check + AddSamples.
var reorderBuffer = new ReorderBuffer(reorderWindow,
    emit: (buf, off, cnt) =>                   // one compressed datagram, released in order
    {
        int bytes = decoder.Decode(buf, off, cnt, pcmScratch);
        EmitSamples(pcmScratch, 0, bytes);
    },
    onLost: () =>                              // fires once per skipped sequence (see ReorderBuffer change)
    {
        int bytes = decoder.Conceal(pcmScratch);   // Opus PLC; PCM impl emits nothing (bytes=0)
        if (bytes > 0) EmitSamples(pcmScratch, 0, bytes);
    });
```

**Opus needs its own reorder window (a fixed bounded count).** The existing `ComputeReorderWindow(format)`
sizes the window as a *packet count* scaled by the PCM **byte rate**, calibrated for **MTU-sized PCM
datagrams** (~130–200 pkt/s). For Opus the decoded `format` is still 48 kHz/16-bit/stereo, so it returns
the same **8 packets** — but Opus's packet cadence is the *frame rate* (1000/frame ms), so 8 packets is
the wrong give-up *time*: 80 ms at 10 ms frames, and **160 ms at 20 ms — past the 150 ms drift cap,
which would fire first and defeat concealment.** A frame-duration formula (`ceil(targetMs/frameMs)`)
isn't usable either: the receiver doesn't know the sender's frame size before it decodes. So the Opus
path uses a **fixed constant window of 4 packets** (`OpusReorderWindowPackets`), which keeps give-up
time bounded at **10–80 ms across 2.5–20 ms frames** — always under the drift cap, and ~40 ms at the
10 ms default (matching the PCM path's intent). The PCM path keeps `ComputeReorderWindow` unchanged.

**PLC needs the learned frame length.** Opus concealment (`Decode(null, …)`) must be told how many
samples to synthesise, but the sender's frame size isn't signalled. The decoder adapter therefore
**remembers the sample count from its most recent successful decode** and conceals with that length.
The first datagram is always a real decode (it establishes the reorder baseline), so a `Conceal` never
fires before a length is known.

### 6. `Streaming/ReorderBuffer.cs` — additive `onLost` hook

Today, when the window fills, the buffer skips missing sequences:
`while (held.Count >= maxDepth) { nextSeq++; EmitContiguous(); }` — which can abandon **several**
sequences in a single `Add`. Add an optional `onLost` delegate fired **once per skipped sequence**
(at each `nextSeq++` that abandons a missing packet), so the Opus decoder emits exactly one concealment
frame per lost frame. Firing per-skip (not once per `Add`) is essential: Opus decode is stateful, so
the decoder must advance by exactly the number of lost frames or every subsequent frame desyncs. This
is **additive** — `onLost` defaults to a no-op, so the PCM path and every existing call site are
unchanged, and the PCM decoder's `Conceal` returns 0 bytes (today's "skip the gap" behaviour,
byte-for-byte).

**Loss handling — PLC only, no in-band FEC.** Opus's in-band FEC is **LBRR**, a mechanism inside the
**SILK** (speech) sub-codec. It cannot apply here, for two independent reasons:
1. We run `RESTRICTED_LOWDELAY` = **CELT-only**; CELT has no LBRR path, so `SetInbandFec(1)` is a no-op.
2. Even in `OPUS_APPLICATION_AUDIO`, fullband stereo at 192 kbps is coded by **CELT** (SILK can't do
   fullband) — so LBRR still isn't produced. Forcing SILK/hybrid would mean capping bandwidth/bitrate,
   i.e. sacrificing the music quality we're streaming for.

So FEC is not an available lever for this content, regardless of application mode. The loss tool is
**PLC** — decoder-side, works in CELT-only, needs no encoder support or bandwidth, and fills a gap by
synthesising a plausible continuation. It *guesses* (lossy, degrades on long/complex losses) rather
than *reconstructing*, which is the only Opus option for low-latency fullband music.

### 7. UI — `UI/MainWindow.xaml` + `.cs` (minimal)

Add two selectors to `SenderPanel`, both value-backed like the format dropdowns (200-width, using the
existing `FormatOption(int Value, string Label)` + `SelectValue` plumbing):
- **Codec** — 2-item `ComboBox` (PCM / Opus). When Opus is selected, **disable** the Sample Rate / Bits
  / Channels dropdowns and enable the frame-size dropdown (the session fixes 48 kHz/16-bit/stereo);
  invert on PCM.
- **Opus frame size** — 4-item `ComboBox` mapping label → `OpusFrameSamples`: `2.5 ms`→120, `5 ms`→240,
  `10 ms`→480 (default), `20 ms`→960.

This dynamic per-control gating is **new plumbing** (today the only enable/disable is the blanket
`SettingsPanel.IsEnabled` toggled by `SetRunningState`). It needs a `Codec` `SelectionChanged` handler
(analogous to `ModeComboBox_SelectionChanged`) that applies the gating; the same gating must also run in
`PopulateUIFromConfig` (initial state) and be re-asserted after `SetRunningState(false)` re-enables
`SettingsPanel` on Stop, so the Opus-disabled state isn't clobbered.
`UpdateConfigFromUI`/`PopulateUIFromConfig` read/write `Config.Codec` and `Config.OpusFrameSamples` via
`SelectedValue`/`SelectValue` (int-valued; cast to/from `CodecId` for the codec — see §2). The receiver
needs no UI. Both selectors live in `SenderPanel`, so they're hidden in Receiver mode by the existing
`UpdateModePanels` logic.

### 8. Diagnostics (small, optional)

Prepend `codec=opus` to the status/diagnostics line (touches both `DiagnosticsSnapshot.ForSender` and
`ForReceiver`). The existing `lost/s`/`reorder/s` meters already cover the wire; `underrun/s` still
reflects drain-side clock drift. A dedicated `plc/s` counter is optional polish (would extend
`DiagnosticsSnapshot`).

## Data flow

```
Sender: PCM capture (effective 48k/16/stereo for Opus) → encoder.Push
  PCM:  MTU slice                 → datagram[3+] = PCM frame
  Opus: accumulate one frame → Encode → datagram[3+] = Opus frame
  datagram[0]=format code, [1]=codec id, [2]=sequence

Receiver: datagram[1] → ReadCodec → build PcmCodec | OpusDecoder (once; re-checked per datagram)
  in-order release → decoder.Decode → PCM → BufferedWaveProvider
  window-fill gap  → decoder.Conceal → PLC PCM (Opus) | nothing (PCM)
  → UnderrunCountingWaveProvider → WasapiOut   (pipeline unchanged)
```

## Error handling / edge cases

- **Old/hand-edited config without `Codec`:** defaults to `Pcm` (LoadConfig keeps the in-memory
  default). No breakage.
- **Opus on a hi-res/multichannel system:** no special case — the WASAPI engine resamples/downmixes to
  48 kHz/16-bit/stereo at capture, so the wire always carries valid stereo Opus.
- **Receiver sees an unknown codec id** (foreign datagram / version mismatch): logged once and the
  datagram is ignored — identical pattern to the existing unknown-format-code guard.
- **Capture auto-recovery (RestartCapture):** the encoder is rebuilt with the capture; the Opus
  accumulator restarts empty (a one-frame discontinuity at resume, same class as the existing
  sequence-restart blip — acceptable). Because Opus forces stereo, a device change does not alter the
  wire format.
- **Output auto-recovery (RestartOutput):** untouched — the decoder lives in the receive loop, not the
  output path; `BuildAndPlayOutput` still flushes to the live edge.
- **Latency cap / drift control:** unchanged — it operates on decoded PCM backlog. Opus's algorithmic
  delay adds a small *fixed* latency on top of the buffer backlog; it does not interact with the cap.
- **Reorder window vs Opus packet cadence:** `ComputeReorderWindow` is calibrated for PCM datagram
  cadence; reused unchanged for Opus it mis-sizes the give-up time (up to 160 ms at 20 ms frames, past
  the drift cap). The Opus path uses a fixed 4-packet window instead (bounded 10–80 ms; see §5).
- **CPU / GC:** OpusSharp adds no per-call allocation and ~53 µs encode / ~15 µs decode (≈1 % of the
  10 ms budget) — see benchmark. Neither is a concern.

## Backward / cross-version compatibility

**Breaking wire change** (new codec byte; sequence offset shifts 1 → 2). Old and new builds cannot
interoperate — acceptable under the existing "both sides must run the same version" rule (CLAUDE.md,
`WireProtocol`; the catalog order is already version-locked). `config.json` is **not** broken: the
schema only gains optional fields that default safely.

If a non-breaking rollout were ever required (not a goal here), the codec byte could instead steal the
high bit of an otherwise-unused header position — rejected as hacky; a clean explicit byte is worth the
version bump given the same-version rule already holds.

## Out of scope

- Per-session **bitrate** UI (constant 192 kbps for now). Frame size **is** exposed (see UI); bitrate is
  not.
- In-band FEC — not applicable to this content (SILK-only mechanism; see Loss handling).
- Compression of hi-res/multichannel as such — selecting Opus *converts* to 48 kHz/16-bit/stereo by
  design; users who want bit-exact hi-res stay on PCM.
- A second lossless codec (FLAC) — noted as the answer if bit-exact hi-res is ever wanted.
- Hand-rolled libopus P/Invoke — OpusSharp already provides the binding and ships the binaries.
- Receiver-side codec selection UI (receiver adapts to the header by design).

## Verification

No test project exists. Plan-time verification:
- Build clean (0/0) with the OpusSharp package restored; confirm the native `opus.dll` loads (a missing
  native throws `DllNotFoundException` on first encode).
- **PCM regression (must be byte-identical behaviour):** with `Codec = Pcm`, run a Sender↔Receiver loop;
  confirm audio, diagnostics, latency cap, and both auto-recovery paths behave exactly as today. (The
  codec byte is the only wire difference.)
- **Opus happy path:** select Opus; confirm the format dropdowns disable, the sender uses
  48 kHz/16-bit/stereo regardless of the source device's native format, the receiver logs `codec=opus`,
  audio plays at correct pitch, and `KB/s` drops to ~10–15 % of PCM.
- **Format not clobbered:** with non-default PCM format fields set, toggle Opus on then off; confirm the
  original `SampleRate`/`Bits`/`Channels` are restored.
- **Opus on a non-48k / multichannel source:** play 44.1 kHz and (if available) 5.1 content; confirm the
  WASAPI engine converts it and the stream stays valid stereo Opus.
- **Device-flip during Opus:** trigger a default-device change / monitor-sleep mid-stream; confirm the
  session stays 48 kHz/16-bit/stereo Opus across the rebuild.
- **Opus frame-size setting:** change the Sender frame-size dropdown across 2.5/5/10/20 ms; confirm the
  receiver plays correctly with no receiver-side change, and that the live `pkts/s` diagnostic tracks the
  choice (10 ms → ~100/s, 5 ms → ~200/s). Verify an out-of-range `OpusFrameSamples` clamps to the
  nearest valid option.
- **Loss concealment:** induce loss (or rely on natural Wi-Fi loss); confirm `lost/s` ticks while audio
  degrades gracefully (Opus PLC) rather than clicking.
- A throwaway console check (optional, no framework): round-trip a known 48 kHz/16-bit/stereo frame
  through `OpusEncoderAdapter`→`OpusDecoderAdapter` and assert sample count / rough RMS similarity, and
  that `PcmCodec` is a bit-exact identity.

### Library benchmark

Standalone harness (OpusBench — not part of the app), Concentus (managed) vs OpusSharp (native), at
48 kHz/16-bit/stereo, 10 ms, 192 kbps, complexity 10. Dev machine (.NET 10, x64, workstation GC),
100k timing / 300k soak:

| metric | Concentus (managed) | OpusSharp (native) |
|---|---|---|
| encode alloc / call | 75,645 B | **0 B** |
| decode alloc / call | 18,023 B | **0 B** |
| soak GC gen0/1/2 (300k) | 560 / 23 / 0 | **0 / 0 / 0** |
| encode mean / p99 (µs) | 66.5 / 77.6 | 53.1 / 73.0 |
| decode mean / p99 (µs) | 25.1 / 30.2 | 15.1 / 22.0 |

OpusSharp allocates zero on the audio thread (the decisive metric) and is faster; both are ~1 % of the
10 ms budget. Numbers are hardware-dependent, but the 0-vs-93KB shape drove the decision. The same
harness produced the application-mode (+4 ms for AUDIO) and frame-size numbers cited in Decisions.

## Docs to update (part of the work)

- `CLAUDE.md`: the wire-protocol paragraph (new codec byte + offsets), the Sender/Receiver paragraphs
  (encoder accumulator sized by `OpusFrameSamples` / decoder + PLC; Opus uses an effective
  48 kHz/16-bit/stereo format; frame size not signalled — receiver reads it from the packet), the
  `Config` list (`Codec` + `OpusFrameSamples`), and the MainWindow UI paragraph (Codec + Opus-frame-size
  dropdowns and their gating).
- `docs/2026-06-27-streaming-review-followups.md`: add the new "Wire protocol (current)" layout (3-byte
  header) if/when this lands.
