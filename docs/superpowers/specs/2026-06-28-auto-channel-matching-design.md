# Auto Channel Matching (source-following capture + dynamic receiver) — Design

Date: 2026-06-28
Status: design (awaiting review → plan)

## Goal

Stop the sender from force-converting the captured channel count to a fixed value. Add an
**"Auto (match source)"** channel option that captures at the render device's *native* channel
count (snapped to a wire-representable value), so a stereo source streams bit-exact and a 5.1/7.1
source streams as-is — and make the receiver **rebuild its pipeline** when the wire format changes
mid-stream, so the feature stays truthful across device-change recoveries.

## Problem

Selecting **6** or **8** channels on the sender while the system audio mix is **stereo** produces
audibly **quieter** playback. Root cause (confirmed: the streaming path does *zero* sample
manipulation — pure PCM passthrough): the audio is matrix-converted twice by WASAPI —

1. **Sender up-mix (2 → 6/8).** `AudioClientStreamFlags.AutoConvertPcm` on the loopback capture
   ([SenderSession.cs:283](../../../Streaming/SenderSession.cs)) makes the shared-mode engine
   up-mix the stereo mix to the requested channel count. Windows' up-mix matrix routes L/R into
   the front pair (rest silent) and **attenuates** while doing so.
2. **Receiver down-mix (6/8 → 2).** `WasapiOut` on a stereo render device
   ([ReceiverSession.cs:110](../../../Streaming/ReceiverSession.cs)) down-mixes back to stereo using
   the standard ITU/AC-3 coefficients, which include headroom attenuation.

Two lossy matrix passes → quieter output, plus 3–4× the bandwidth for no benefit. The channel
count should follow the source instead of being a blind fixed request.

A second, latent problem this surfaces: the receiver locks its pipeline to the format read from the
**first** packet ([ReceiverSession.cs:202](../../../Streaming/ReceiverSession.cs)) and never re-reads
it. Any mid-stream format change (which Auto makes likely — see below) would be misinterpreted as
the old format → garbled playback.

## Locked decisions

- **Auto is exposed as a dropdown option**, not a reinterpretation of the numeric values. Numeric
  picks (1/2/6/8) remain **hard requests** (force exactly that count, up/down-mixing as today, for
  users who deliberately want it). `Config.Channels = 0` is the **Auto sentinel**.
- **Auto is channels-only.** Sample rate and bit depth stay as the user's explicit selection — the
  quiet artifact is purely a channel-matrix effect; resample / bit-depth conversion don't have it.
- **Auto resolution rule:** capture at the **largest catalog channel count ≤ the device's native
  mix channel count** (never up-mix; snap down to a wire-representable value). Examples: native
  6→6, 8→8, 2→2, 4→2, 1→1.
- **What "native" means — endpoint configuration, not audio content.** Auto reads
  `MMDevice.AudioClient.MixFormat.Channels`, the WASAPI **shared-mode mix format** of the default
  render endpoint. That count is fixed by the Windows endpoint configuration (Sound → Configure
  speakers + Advanced default format); it does **not** vary with what is currently playing. Loopback
  capture only ever sees the engine's post-mix output (already at the mix format), so the original
  content's channel count is unobservable by design — e.g. a stereo song on a 5.1-configured endpoint
  is up-mixed to 6ch *by Windows* and captured as 6ch. Auto therefore streams exactly what the
  endpoint mix produces with **no extra AudioStreamer-added conversion**; it does not (and cannot)
  down-size a 5.1 endpoint to stereo just because the current track is stereo. This is the intended
  behavior: it eliminates the *extra* up/down-mix pair that caused the quiet-audio bug, without
  second-guessing the user's Windows speaker configuration.
- **Auto is the default for fresh configs.** Existing `config.json` files keep their stored value
  until next changed.
- **Receiver rebuilds its pipeline on a valid mid-stream format-code change** (reusing the existing
  `BuildAndPlayOutput`/`outputLock` machinery), cutting over cleanly at the live edge.

## Architecture / components

### 1. `Core/AudioStreamerLogic.cs` — `Config.Channels` sentinel + default

- `Config.Channels` default becomes `0` (Auto). `0` is the **Auto sentinel** — never a real
  channel count, serializes cleanly as `"Channels": 0`.
- No schema change otherwise; `SampleRate`/`BitsPerSample` unchanged.
- Note the existing `LoadConfig` behavior (CLAUDE.md): an existing valid file that predates this
  change keeps its stored `Channels` (e.g. `2`) — only fresh/regenerated files get the `0` default.

### 2. `Streaming/AudioFormats.cs` — Auto resolution helper

Add the sentinel constant and a pure resolver. No change to the catalog `Formats` table or the wire
contract (Auto is never encoded — the sender always sends a concrete resolved code).

```csharp
public const int AutoChannels = 0;   // Config.Channels sentinel: match the source

/// <summary>
/// Resolves a configured channel count to a concrete catalog value for capture. For an explicit
/// request, returns it unchanged. For <see cref="AutoChannels"/>, returns the largest catalog
/// channel count ≤ the device's native count — never up-mixing, always snapping down to a
/// wire-representable value (native 6→6, 8→8, 4→2, 2→2, 1→1).
/// </summary>
public static int ResolveCaptureChannels(int configuredChannels, int deviceNativeChannels)
{
    if (configuredChannels != AutoChannels)
        return configuredChannels;

    int best = ChannelCounts[0];                 // smallest catalog value as the floor
    foreach (int c in ChannelCounts)
        if (c <= deviceNativeChannels && c > best)
            best = c;
    return best;
}
```

`ChannelCounts` is `{ 1, 2, 6, 8 }`, so the floor (`ChannelCounts[0]` = 1) guarantees a valid result
even for an unexpectedly small native count.

### 3. `Streaming/SenderSession.cs` — resolve channels at capture build

In `StartCapture`, before building the `WaveFormat`, read the default render device's native channel
count and resolve. The device's `MixFormat` is already reachable here — `LogCaptureFormat` already
enumerates it ([SenderSession.cs:157](../../../Streaming/SenderSession.cs) /
[:250](../../../Streaming/SenderSession.cs)); the resolution must happen **before** the
`TweakedWasapiLoopbackCapture` build so the capture format and the wire code agree.

```csharp
// Inside StartCapture, before constructing the capture (still under senderLock):
int nativeChannels = GetDefaultRenderChannels();                       // MMDevice MixFormat.Channels
int channels = AudioFormats.ResolveCaptureChannels(config.Channels, nativeChannels);

capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
{
    WaveFormat = AudioFormats.ToWaveFormat(config.SampleRate, config.BitsPerSample, channels)
};
...
WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(config.SampleRate, config.BitsPerSample, channels));
```

- `GetDefaultRenderChannels()` enumerates the default render endpoint via `MMDeviceEnumerator`
  (`DataFlow.Render`, `Role.Multimedia`) and returns `device.AudioClient.MixFormat.Channels`. On any
  COM/enumeration failure it falls back to `AudioFormats.DefaultChannels` (2) so capture still builds
  — Auto degrades to stereo rather than throwing. The same enumeration already happens in
  `LogCaptureFormat`; factor out the device-acquisition so it isn't done twice (acquire the device /
  native channel count once, use it for both resolution and logging).
- **Locking-window note (review I2):** today the *only* device enumeration is `LogCaptureFormat`,
  which is deliberately called **outside `senderLock`** ([SenderSession.cs:153-157](../../../Streaming/SenderSession.cs))
  because COM endpoint enumeration "can briefly block during a device-change storm" and must not stall
  a UI-thread `Stop()`. Auto's resolution must run **before** the `WaveFormat` is built, i.e. **inside
  `senderLock`** — and `RestartCapture` calls `StartCapture` already holding the lock. So this is **not**
  a no-op on locking characteristics: a device-change storm can now hold `senderLock` slightly longer,
  marginally widening the window where a UI `Stop()` blocks. **Resolution:** acquire the `MMDevice` /
  read `MixFormat.Channels` at the **top of `StartCapture` before taking `senderLock`** where the call
  path allows (the first-start path from `Start()`), pass the resulting `int` across the lock boundary,
  and reuse it for `LogCaptureFormat(wireFormat, nativeChannels)` outside the lock. On the
  `RestartCapture` path the lock is already held by the caller, so that enumeration unavoidably runs
  under the lock; this is accepted (the recovery poll is off the UI thread and the enumeration is fast
  in the common case). The plan must not claim the lock window is unchanged.
- Because `StartCapture` runs on every (re)build including `RestartCapture` (device-change recovery),
  Auto **re-resolves automatically** when the default device changes — this is what makes the wire
  channel count dynamic.
- `LogCaptureFormat` already logs native vs sent format; with Auto the "sent" side now reflects the
  resolved count, so a stereo-source session visibly sends 2ch.

### 4. `Streaming/ReceiverSession.cs` — dynamic pipeline rebuild on format change

Today the format-dependent state is split: `InitializeReceiver` builds `bufferedWaveProvider` +
`underrunMeter` then calls `BuildAndPlayOutput`; `ReceiveAudio` builds the `ReorderBuffer` as a
method-local. Consolidate the format-dependent build so both the first-packet path and a mid-stream
change use one path, and track the active code.

**New fields:**
- `private byte currentFormatCode;` — the active wire format code.
- `private bool loggedBadFormatMidStream;` — log-once guard for invalid codes seen in the
  `ReceiveAudio` loop. **(Review M2:** the existing `loggedBadFormat` is a *local* in
  `InitializeReceiver` ([ReceiverSession.cs:180](../../../Streaming/ReceiverSession.cs)) and is **not**
  reachable from `ReceiveAudio`, so the mid-stream guard needs its own field — do not assume the
  existing flag can be reused.)

**New method** `BuildPipeline(AudioFormats.Format fmt, byte code)` — sets `currentFormatCode = code`,
(re)builds the `bufferedWaveProvider` **field** (from `ToWaveFormat`), wraps it in a fresh
`underrunMeter` **field**, and calls `BuildAndPlayOutput()` (which already disposes any prior
`WasapiOut` under `outputLock` and re-inits on the current `underrunMeter`). Returns nothing; callers
rebuild their `ReorderBuffer` from the new `bufferedWaveProvider.WaveFormat`.

- `InitializeReceiver`: replace the inline `bufferedWaveProvider`/`underrunMeter`/`BuildAndPlayOutput`
  block with `BuildPipeline(fmt, code)`. Because this runs **before** `ReceiveAudio` starts,
  `currentFormatCode` already equals the active code by the time the steady-state loop runs, so the
  first datagram never triggers a spurious second build **(review M4)**.
- `ReceiveAudio`: hoist the `ReorderBuffer` to a reassignable local. On each datagram, read the code;
  if it's **valid and `!= currentFormatCode`**, perform the rebuild **before processing (`Add`-ing)
  the triggering datagram's payload**, in this exact order:
  1. log the transition (e.g. `"Wire format changed: 6ch → 2ch; rebuilding output."`),
  2. `BuildPipeline(fmt, code)` — reassigns the `bufferedWaveProvider`/`underrunMeter` fields and
     rebuilds the output,
  3. `reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat)`,
  4. `reorderBuffer = new ReorderBuffer(reorderWindow, …)` — a **fresh instance**,

  then feed the triggering datagram to the **new** `reorderBuffer`. **(Review C1 — ordering, not a
  stale local:** the emit closure at [ReceiverSession.cs:231-236](../../../Streaming/ReceiverSession.cs)
  captures the `bufferedWaveProvider` **field** via `this`, so it already reads the *current* buffer
  live — it does **not** capture a stale local. The hazard is therefore purely **ordering**: the
  rebuild (steps 1–4) must complete before any datagram is `Add`-ed, so no old-`ReorderBuffer` `Add`
  ever lands old-format-aligned bytes in the new buffer. Since the whole loop body is single-threaded,
  doing the rebuild before the triggering datagram's `Add` is sufficient — there is no torn
  intermediate state.)
  An **invalid** code is ignored exactly as the first-packet guard does (logged once via the new
  `loggedBadFormatMidStream` field), with **no** rebuild.
- **Sequence baseline across a rebuild (review I1):** the fresh `ReorderBuffer` starts with no
  baseline and **re-baselines on its first post-rebuild datagram's sequence byte**
  ([ReorderBuffer.cs:27-31](../../../Streaming/ReorderBuffer.cs)), so the per-datagram sequence-unwrap
  math does not break across the format gap. In the genuine device-flap case the sender's `sequence`
  also restarts at 0 (it resets per capture rebuild — [SenderSession.cs:100](../../../Streaming/SenderSession.cs));
  in the spurious-foreign-datagram case the real sender's sequence keeps climbing and the new
  `ReorderBuffer` simply re-baselines on the next packet. Either way the net effect is the "brief audio
  reset" characterized below — not corruption.
- The old buffer is discarded by reassignment and `BuildAndPlayOutput` clears to the live edge, so the
  cutover drops the residual old-format audio rather than playing it back at the wrong layout.

**Concurrency:** `BuildPipeline` runs on the receive loop thread (the same thread that calls
`AddSamples`), and `BuildAndPlayOutput` already serializes `WasapiOut` build/teardown under
`outputLock` with the in-lock `isRunning` re-check, so a mid-stream rebuild composes safely with the
`PlaybackStopped` recovery path (Task-5 discipline) — no new lock needed. Setting the new
`bufferedWaveProvider`/`underrunMeter` fields **before** calling `BuildAndPlayOutput` ensures the new
`WasapiOut` is wired to the new buffer.

### 5. UI — `UI/MainWindow.xaml.cs`

- `PopulateFormatDropdowns()`: prepend `new FormatOption(AudioFormats.AutoChannels, "Auto (match
  source)")` to the Channels combo's item list (ahead of the catalog `ChannelCounts`). Sample-rate
  and bit-depth combos unchanged.
- `PopulateUIFromConfig`: `SelectValue(ChannelsCombo, config.Channels, AudioFormats.AutoChannels)` —
  `0` selects the Auto item; a stored numeric selects its item; an unknown value falls back to Auto.
  The fallback for the Channels combo changes from `DefaultChannels` (2) to `AutoChannels` (0); since
  the Auto item is prepended to the list, `0` is always a found item, so the helper's "fallback never
  runs" invariant still holds — but **update the now-stale comment at
  [MainWindow.xaml.cs:191](../../../UI/MainWindow.xaml.cs)** ("fallback is always a catalog default")
  so it no longer implies the fallback is necessarily a catalog value **(review I3)**.
- `UpdateConfigFromUI`: `config.Channels = SelectedValue(ChannelsCombo, AudioFormats.AutoChannels)` —
  reads `0` for Auto, the int for an explicit pick. No `ParseOr` (selection-only stays always valid).
- `ChannelLabel` is unaffected — the Auto label is supplied directly at the dropdown, not via the
  catalog label helper (which only maps real counts).

## Data flow

```
Sender:
  config.Channels (0=Auto | 1/2/6/8)
    + device native MixFormat.Channels
      → AudioFormats.ResolveCaptureChannels → concrete count (e.g. 2)
        → ToWaveFormat (capture) AND ToCode (wire header byte 0)
  device change → RestartCapture → StartCapture → re-resolves → wire format may change

Receiver:
  datagram[0] = format code
    code == currentFormatCode → stream as-is
    code valid & != current   → log, BuildPipeline(new fmt) + rebuild ReorderBuffer (live-edge cutover)
    code invalid              → log once, ignore datagram (no rebuild)
```

## Error handling / edge cases

- **Device enumeration fails on the sender** (COM error, no default endpoint): `GetDefaultRenderChannels`
  returns `DefaultChannels` (2); Auto degrades to stereo, capture still builds.
- **Native count not catalog-representable** (e.g. a 4-channel quad device): `ResolveCaptureChannels`
  snaps **down** to the largest catalog value ≤ native (4→2). Never up-mixes.
- **Existing config with explicit `Channels`** (e.g. `2` or `6`): preserved and treated as a hard
  request — no behavior change for users who chose a specific count.
- **Spurious foreign datagram carrying a valid-but-different code:** could trigger one spurious
  receiver rebuild (brief audio reset). This is the same exposure as today's "foreign packet enters
  the buffer," so it's **accepted** rather than mitigated with packet authentication — noted, not
  fixed.
- **Rapid device flapping** (monitor toggling): each genuine change rebuilds both ends; rebuilds clear
  to the live edge, so the cost is a short audio reset per change, not corruption.

## Backward / cross-version compatibility

- **Wire protocol unchanged.** Auto is resolved to a concrete catalog code before transmission; the
  header layout, catalog order, and code semantics are identical. New and old *receivers* both accept
  the concrete code. (Old receivers still don't rebuild mid-stream — but that's their existing
  limitation, not a new break.)
- **`config.json` compatible.** `Channels` is still an int; `0` is a new legal value. Old files keep
  their explicit value; the in-memory default for fresh files is `0`.

## Out of scope

- Auto for **sample rate / bit depth** (channels-only this round; rate/depth conversion lacks the
  quiet artifact).
- **4-channel (quad) wire support** — not in the catalog; Auto snaps it down to stereo.
- Packet authentication to reject foreign datagrams (the spurious-rebuild edge is accepted).
- Receiver-mode UI (the receiver still derives format from the wire; its format fields stay hidden).

## Verification

No test project exists. Plan-time verification:

- Build clean (0/0).
- **Primary regression (the reported bug):** with a stereo system-audio source, select **Auto** on the
  sender; confirm the sender logs a resolved **2ch** send, the receiver logs **Channels: 2**, and
  playback level matches a native 2ch selection (no quiet up/down-mix).
- **Explicit still forces:** select **6** on the same stereo source; confirm it still sends 6ch (hard
  request preserved) — i.e. Auto didn't silently override explicit picks.
- **Dynamic rebuild:** while streaming Auto, change the sender's default render device (or sleep the
  HDMI monitor so it falls back to a stereo endpoint); confirm the sender re-resolves, the receiver
  logs a format-change rebuild, and audio resumes at the new channel count without restart.
- **Receiver guard intact:** an unknown/foreign code is still logged once and ignored (no rebuild
  storm).
- Optional throwaway console check: `ResolveCaptureChannels(0, n)` for n ∈ {1,2,3,4,6,8} returns
  {1,2,2,2,6,8}; explicit values pass through unchanged.

## Docs to update (part of the work)

- `CLAUDE.md`: the MainWindow UI paragraph (Auto channel option) and the Receiver paragraph
  (dynamic pipeline rebuild on mid-stream format change); note the sender's Auto resolution in the
  Sender paragraph.
- `docs/2026-06-27-streaming-review-followups.md`: cross-reference if a related item exists.
