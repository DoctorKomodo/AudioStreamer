# Auto Channel Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Auto (match source)" sender channel option that captures at the render endpoint's mix-format channel count (never up-mixing), and make the receiver rebuild its pipeline when the wire format changes mid-stream.

**Architecture:** `Config.Channels = 0` is an Auto sentinel. The sender resolves it to a concrete catalog channel count from the device's `MixFormat.Channels` at every capture build (so it re-resolves on device-change recovery). The wire always carries a concrete catalog code — Auto never goes on the wire. The receiver gains a single `BuildPipeline(fmt)` entry point reused by first-packet init and a mid-stream format-change rebuild, composing with the existing `outputLock` discipline.

**Tech Stack:** .NET 10 / `net10.0-windows`, WPF, NAudio (WASAPI loopback capture + `WasapiOut`), single `AudioStreamer` namespace.

**Spec:** `docs/superpowers/specs/2026-06-28-auto-channel-matching-design.md` (read it; it carries the rationale and the review findings folded in).

## Global Constraints

- **Wire protocol unchanged.** Auto is resolved to a concrete catalog code *before* transmission; header layout, catalog order, and code semantics are identical. Do not add wire fields.
- **`config.json` schema unchanged.** `Channels` stays an `int`; `0` is a new legal value (Auto). Existing files keep their stored value.
- **Real-time audio hot paths.** The sender `DataAvailable` handler and the receiver `ReceiveAudio` loop must stay allocation-free and lock-free on the steady-state (no-format-change) path. The pipeline rebuild may allocate/lock — but only fires on an actual format change.
- **Culture-invariant formatting** (this is not a single-user en-US machine): default integer formatting (no group separators) is fine; never introduce locale-dependent number formatting.
- **Fully-qualify WinForms types** (`System.Windows.Forms.*`); the UI helpers already qualify `System.Windows.Controls.ComboBox` to dodge the `UseWindowsForms` ambiguity — keep that.
- Nullable reference types are enabled; the `?.`-guarded session fields are intentional.
- No test project exists. Per-task automated verification is `dotnet build` (expect `0 Warning(s) / 0 Error(s)`); runtime/audio smoke is manual (the user runs it).

---

### Task 1: Auto sentinel + resolver + Config default

**Files:**
- Modify: `Streaming/AudioFormats.cs` (add `AutoChannels`, `ResolveCaptureChannels`)
- Modify: `Core/AudioStreamerLogic.cs:24` (`Config.Channels` default `2` → `0`)

**Interfaces:**
- Produces: `AudioFormats.AutoChannels` (`const int` = 0); `int AudioFormats.ResolveCaptureChannels(int configuredChannels, int deviceNativeChannels)` — returns an explicit count unchanged, or for the sentinel returns the largest `ChannelCounts` value ≤ `deviceNativeChannels` (floored at `ChannelCounts[0]`). Consumed by Task 2 (sender) and Task 4 (UI fallback value).

- [ ] **Step 1: Add the sentinel + resolver to `AudioFormats`**

In `Streaming/AudioFormats.cs`, add the constant next to the existing `Default*` constants (after line 19):

```csharp
        // Config.Channels sentinel: capture at the render endpoint's mix-format channel count instead of a fixed
        // value. Never appears on the wire — the sender resolves it to a concrete catalog code before sending.
        public const int AutoChannels = 0;
```

And add the resolver (place it just after `FromCode`, around line 54):

```csharp
        /// <summary>
        /// Resolves a configured channel count to a concrete catalog value for capture. An explicit request is
        /// returned unchanged. <see cref="AutoChannels"/> resolves to the largest catalog channel count
        /// &lt;= <paramref name="deviceNativeChannels"/> — never up-mixing, snapping down to a wire-representable
        /// value (native 6→6, 8→8, 4→2, 2→2, 1→1). The floor is <c>ChannelCounts[0]</c>, so even a bogus small
        /// native count yields a valid result. "Native" here is the WASAPI shared-mode mix-format channel count
        /// (endpoint configuration), not the currently-playing content — see the design doc.
        /// </summary>
        public static int ResolveCaptureChannels(int configuredChannels, int deviceNativeChannels)
        {
            if (configuredChannels != AutoChannels)
                return configuredChannels;

            int best = ChannelCounts[0];
            foreach (int c in ChannelCounts)
                if (c <= deviceNativeChannels && c > best)
                    best = c;
            return best;
        }
```

- [ ] **Step 2: Change the `Config.Channels` default to Auto**

In `Core/AudioStreamerLogic.cs:24`, change:

```csharp
            public int Channels { get; set; } = 2;
```

to:

```csharp
            // 0 == AudioFormats.AutoChannels: match the render endpoint's mix-format channel count. Existing
            // config.json files keep their stored explicit value (LoadConfig only adds missing fields).
            public int Channels { get; set; } = 0;
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 4: Verify the resolver truth table (reasoning check — pure deterministic logic, no framework)**

Confirm by inspection that `ResolveCaptureChannels` with `ChannelCounts = { 1, 2, 6, 8 }` yields:

| configured | native | result | why |
|-----------|--------|--------|-----|
| 0 (Auto) | 1 | 1 | floor |
| 0 (Auto) | 2 | 2 | exact |
| 0 (Auto) | 3 | 2 | snap down |
| 0 (Auto) | 4 | 2 | snap down (4 not in catalog) |
| 0 (Auto) | 6 | 6 | exact |
| 0 (Auto) | 8 | 8 | exact |
| 0 (Auto) | 0 | 1 | floor (degenerate) |
| 6 (explicit) | 2 | 6 | explicit passes through unchanged |

- [ ] **Step 5: Commit**

```bash
git add Streaming/AudioFormats.cs Core/AudioStreamerLogic.cs
git commit -m "Add Auto channel sentinel + ResolveCaptureChannels; default Channels to Auto"
```

---

### Task 2: Sender resolves Auto at capture build

**Files:**
- Modify: `Streaming/SenderSession.cs` (resolve channels in `StartCapture`; acquire render info once; rework `LogCaptureFormat`)

**Interfaces:**
- Consumes: `AudioFormats.ResolveCaptureChannels` (Task 1), `AudioFormats.DefaultChannels`.
- Produces: nothing new for later tasks; the wire code now reflects the resolved count.

**Context:** `StartCapture` ([SenderSession.cs:65-159](../../../Streaming/SenderSession.cs)) builds the capture under `senderLock`. `RestartCapture` calls `StartCapture` **already holding** `senderLock`; `Start()` calls it without the lock. The native channel count must be read **before** the `WaveFormat` is built and must agree with the wire code (`ToCode`). Per design review I2, acquire the render endpoint **once at the top of `StartCapture`, before `lock (senderLock)`** (so it's lock-free on the first-start path), and pass the result across the lock boundary to logging — avoiding the previous separate enumeration inside `LogCaptureFormat`. On the `RestartCapture` path this acquisition runs under the caller's lock; that is accepted (recovery poll is off the UI thread, enumeration is fast).

- [ ] **Step 1: Add a single render-info acquisition helper**

In `Streaming/SenderSession.cs`, add this helper near `LogCaptureFormat` (replace the body work it used to do). It acquires the default render endpoint once and returns its name + shared-mode mix format (NAudio's `MixFormat` is a managed copy, safe to use after the enumerator is disposed):

```csharp
        // Acquires the default render endpoint once: its friendly name and shared-mode mix format. The mix
        // channel count is what Auto matches (endpoint configuration, not content). Returns null on any
        // COM/enumeration failure so the caller falls back to a stereo capture rather than throwing.
        private static (string Name, WaveFormat Mix)? TryGetDefaultRenderInfo()
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return (device.FriendlyName, device.AudioClient.MixFormat);
            }
            catch
            {
                return null;
            }
        }
```

- [ ] **Step 2: Resolve channels in `StartCapture` and use the resolved count for both capture format and wire code**

In `StartCapture`, the current head is:

```csharp
        private void StartCapture()
        {
            TweakedWasapiLoopbackCapture capture;
            lock (senderLock)
            {
                capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = AudioFormats.ToWaveFormat(config.SampleRate, config.BitsPerSample, config.Channels)
                };
```

Change it to acquire render info before the lock, resolve the channel count, and use the resolved `channels` for both `ToWaveFormat` and (further down) `ToCode`:

```csharp
        private void StartCapture()
        {
            // Acquire the render endpoint once, before senderLock (lock-free on the first-start path; under the
            // caller's lock on the RestartCapture path — accepted). MixFormat.Channels is what Auto matches.
            var renderInfo = TryGetDefaultRenderInfo();
            int nativeChannels = renderInfo?.Mix.Channels ?? AudioFormats.DefaultChannels;
            int channels = AudioFormats.ResolveCaptureChannels(config.Channels, nativeChannels);

            TweakedWasapiLoopbackCapture capture;
            lock (senderLock)
            {
                capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = AudioFormats.ToWaveFormat(config.SampleRate, config.BitsPerSample, channels)
                };
```

Then change the format-code write (currently [SenderSession.cs:92](../../../Streaming/SenderSession.cs)) from `config.Channels` to the resolved `channels`:

```csharp
                WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(config.SampleRate, config.BitsPerSample, channels));
```

- [ ] **Step 3: Pass the acquired render info to logging (no second enumeration)**

Change the post-lock logging call (currently `LogCaptureFormat(capture.WaveFormat);` at [SenderSession.cs:157](../../../Streaming/SenderSession.cs)) to:

```csharp
            LogCaptureFormat(renderInfo, capture.WaveFormat);
```

And replace the `LogCaptureFormat` method body ([SenderSession.cs:243-257](../../../Streaming/SenderSession.cs)) with the version that consumes the already-acquired info:

```csharp
        private void LogCaptureFormat((string Name, WaveFormat Mix)? renderInfo, WaveFormat wireFormat)
        {
            if (renderInfo is not { } info)
            {
                logLine($"Sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch "
                      + "(capture device format unavailable)");
                return;
            }

            var mix = info.Mix;
            logLine($"Capture '{info.Name}': device native {mix.Encoding} {mix.SampleRate}Hz {mix.BitsPerSample}bit {mix.Channels}ch; "
                  + $"sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch");
        }
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add Streaming/SenderSession.cs
git commit -m "Sender: resolve Auto channels from endpoint mix format at capture build"
```

---

### Task 3: Receiver dynamic pipeline rebuild on mid-stream format change

**Files:**
- Modify: `Streaming/ReceiverSession.cs` (new fields, `BuildPipeline`, `EmitSamples`, `InitializeReceiver` + `ReceiveAudio` edits)

**Interfaces:**
- Consumes: `AudioFormats.FromCode`, `AudioFormats.ToWaveFormat`, `WireProtocol.ReadFormatCode`, `ComputeReorderWindow`, `BuildAndPlayOutput` (existing).
- Produces: nothing for later tasks.

**Context:** Today the format-dependent build is split — `InitializeReceiver` builds `bufferedWaveProvider` + `underrunMeter` then calls `BuildAndPlayOutput` ([ReceiverSession.cs:201-208](../../../Streaming/ReceiverSession.cs)); `ReceiveAudio` builds the `ReorderBuffer` as a local ([:229-236](../../../Streaming/ReceiverSession.cs)). The emit closure captures the `bufferedWaveProvider` **field** via `this`, so it reads the current buffer live. `BuildAndPlayOutput` is serialized by `outputLock` with an in-lock `isRunning` re-check ([:98-116](../../../Streaming/ReceiverSession.cs)). The whole receive loop is single-threaded, so a rebuild done *before* the triggering datagram is `Add`-ed is atomic w.r.t. `AddSamples`.

- [ ] **Step 1: Add the two new fields**

In `Streaming/ReceiverSession.cs`, after the diagnostics fields (around [:45](../../../Streaming/ReceiverSession.cs)), add:

```csharp
        // The active wire format code, set in BuildPipeline. The receive loop rebuilds when an incoming code differs.
        private byte currentFormatCode;
        // Log-once guard for an unknown code seen mid-stream (the InitializeReceiver guard's flag is a local there).
        private bool loggedBadFormatMidStream;
```

- [ ] **Step 2: Add `BuildPipeline` and the shared `EmitSamples` callback**

Add `BuildPipeline` (single format-dependent build point) just before `InitializeReceiver`, and `EmitSamples` (the ReorderBuffer emit target, factored out so the initial and rebuilt `ReorderBuffer` share one body — reads the `bufferedWaveProvider` field live, so it always targets the current buffer):

```csharp
        // (Re)builds the format-dependent pipeline — BufferedWaveProvider, its underrun meter, and the WasapiOut
        // output — for a catalog format, and records the active wire code. Called from InitializeReceiver (first
        // packet) and from the receive loop on a mid-stream format change (a sender device-change can flip the
        // channel count under Auto). BuildAndPlayOutput clears to the live edge and is serialized by outputLock,
        // so this composes with the PlaybackStopped recovery path. Set the fields BEFORE BuildAndPlayOutput so the
        // new WasapiOut is wired to the new buffer.
        private void BuildPipeline(AudioFormats.Format fmt, byte code)
        {
            currentFormatCode = code;
            bufferedWaveProvider = new BufferedWaveProvider(AudioFormats.ToWaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                DiscardOnBufferOverflow = true
            };
            underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
            BuildAndPlayOutput();
        }

        // ReorderBuffer emit target. Reads the bufferedWaveProvider field live, so a rebuild that swaps the field
        // re-points this without rebuilding the delegate. Shared by the initial and post-rebuild ReorderBuffers.
        private void EmitSamples(byte[] buf, int off, int cnt)
        {
            if (bufferedWaveProvider.BufferedBytes + cnt > bufferedWaveProvider.BufferLength)
                overflows++;
            bufferedWaveProvider.AddSamples(buf, off, cnt);
        }
```

- [ ] **Step 3: Route `InitializeReceiver`'s first-packet build through `BuildPipeline`**

Replace the build block in `InitializeReceiver` ([:201-208](../../../Streaming/ReceiverSession.cs)):

```csharp
                        logLine($"Sample rate: {fmt.SampleRate}, Bit depth: {fmt.BitDepth}, Channels: {fmt.Channels} received from sender");
                        bufferedWaveProvider = new BufferedWaveProvider(AudioFormats.ToWaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels))
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                            DiscardOnBufferOverflow = true
                        };
                        underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
                        BuildAndPlayOutput();
                        Task.Run(ReceiveAudio, token);
                        break;
```

with:

```csharp
                        logLine($"Sample rate: {fmt.SampleRate}, Bit depth: {fmt.BitDepth}, Channels: {fmt.Channels} received from sender");
                        BuildPipeline(fmt, code);   // sets currentFormatCode before ReceiveAudio runs — no first-packet double-build
                        Task.Run(ReceiveAudio, token);
                        break;
```

- [ ] **Step 4: Use `EmitSamples` for the initial `ReorderBuffer`, and add mid-stream format-change handling in `ReceiveAudio`**

In `ReceiveAudio`, replace the initial `ReorderBuffer` construction ([:231-236](../../../Streaming/ReceiverSession.cs)):

```csharp
            var reorderBuffer = new ReorderBuffer(reorderWindow, (buf, off, cnt) =>
            {
                if (bufferedWaveProvider.BufferedBytes + cnt > bufferedWaveProvider.BufferLength)
                    overflows++;
                bufferedWaveProvider.AddSamples(buf, off, cnt);
            });
```

with the factored callback (and make `reorderBuffer`/`reorderWindow` reassignable — they are already locals):

```csharp
            var reorderBuffer = new ReorderBuffer(reorderWindow, EmitSamples);
```

Then replace the body of the `if (received > WireProtocol.HeaderBytes)` block ([:243-255](../../../Streaming/ReceiverSession.cs)) so it detects a format change **before** processing the datagram:

```csharp
                    if (received > WireProtocol.HeaderBytes)
                    {
                        byte code = WireProtocol.ReadFormatCode(receiveBuffer);
                        if (code != currentFormatCode)
                        {
                            if (AudioFormats.FromCode(code) is not { } newFmt)
                            {
                                // Unknown code mid-stream (foreign datagram / version mismatch): ignore, log once,
                                // no rebuild. Steady-state path stays this single compare when code == current.
                                if (!loggedBadFormatMidStream)
                                {
                                    logLine($"Ignoring mid-stream datagram with unknown format code {code}.");
                                    loggedBadFormatMidStream = true;
                                }
                                continue;
                            }

                            // Valid new format: rebuild the whole pipeline BEFORE feeding this datagram, so no
                            // old-format-aligned bytes land in the new buffer. Single-threaded loop => atomic vs AddSamples.
                            logLine($"Wire format changed to {newFmt.SampleRate}Hz {newFmt.BitDepth}bit {newFmt.Channels}ch; rebuilding output.");
                            BuildPipeline(newFmt, code);
                            reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
                            reorderBuffer = new ReorderBuffer(reorderWindow, EmitSamples);
                            logLine($"Reorder window: {reorderWindow} packets");
                        }

                        int payload = received - WireProtocol.HeaderBytes;
                        packets++;
                        payloadBytes += payload;

                        double backlogNow = bufferedWaveProvider.BufferedDuration.TotalMilliseconds;
                        if (backlogNow < minBacklogMs) minBacklogMs = backlogNow;

                        sequenceTracker.OnReceived(receiveBuffer[WireProtocol.SequenceByteOffset]);
                        reorderBuffer.Add(receiveBuffer[WireProtocol.SequenceByteOffset], receiveBuffer, WireProtocol.HeaderBytes, payload);
                        TrimBacklog(dropScratch);
                    }
```

(The fresh `ReorderBuffer` re-baselines its sequence on the next datagram — see `ReorderBuffer.cs:27-31` — so the unwrap math is fine across the gap. Diagnostics counters intentionally aren't reset; the one-interval blip is acceptable.)

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add Streaming/ReceiverSession.cs
git commit -m "Receiver: rebuild pipeline on mid-stream wire format change"
```

---

### Task 4: UI — Auto channel dropdown option

**Files:**
- Modify: `UI/MainWindow.xaml.cs` (`PopulateFormatDropdowns`, `PopulateUIFromConfig` fallback, `SelectValue` comment)

**Interfaces:**
- Consumes: `AudioFormats.AutoChannels` (Task 1).

**Context:** The Channels combo is populated from `AudioFormats.ChannelCounts` ([MainWindow.xaml.cs:183](../../../UI/MainWindow.xaml.cs)); selection round-trips via `SelectValue`/`SelectedValue` on the `FormatOption(int Value, string Label)` record. The combo lives in `SenderPanel` (hidden in Receiver mode — no change needed there).

- [ ] **Step 1: Prepend the Auto item to the Channels dropdown**

In `PopulateFormatDropdowns` ([MainWindow.xaml.cs:179-184](../../../UI/MainWindow.xaml.cs)), change the `ChannelsCombo` line:

```csharp
            ChannelsCombo.ItemsSource   = AudioFormats.ChannelCounts.Select(c => new FormatOption(c, AudioFormats.ChannelLabel(c))).ToList();
```

to prepend the Auto option (the sentinel `0` with a fixed label, ahead of the catalog counts):

```csharp
            ChannelsCombo.ItemsSource   = new[] { new FormatOption(AudioFormats.AutoChannels, "Auto (match source)") }
                .Concat(AudioFormats.ChannelCounts.Select(c => new FormatOption(c, AudioFormats.ChannelLabel(c)))).ToList();
```

- [ ] **Step 2: Change the Channels preselect fallback to Auto**

In `PopulateUIFromConfig` ([MainWindow.xaml.cs:227](../../../UI/MainWindow.xaml.cs)), change the fallback from `DefaultChannels` to `AutoChannels` (an unknown stored value now lands on Auto, the new default):

```csharp
            SelectValue(ChannelsCombo, audioStreamerLogic.CurrentConfig.Channels, AudioFormats.AutoChannels);
```

(`UpdateConfigFromUI` line 211 needs no change — `SelectedValue(ChannelsCombo, cfg.Channels)` already reads `0` for the Auto item.)

- [ ] **Step 3: Update the now-stale `SelectValue` comment**

In `SelectValue` ([MainWindow.xaml.cs:191](../../../UI/MainWindow.xaml.cs)), the fallback can now be the Auto sentinel (which is present in the Channels list), so update the comment:

```csharp
                              ?? options.First();   // the fallback value is always present in the list (catalog default or Auto sentinel), so this last leg never runs — belt-and-suspenders
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `0 Warning(s)` / `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add UI/MainWindow.xaml.cs
git commit -m "UI: add Auto (match source) channel option"
```

---

### Task 5: Documentation

**Files:**
- Modify: `CLAUDE.md` (Sender, Receiver, and MainWindow UI paragraphs)

**Interfaces:** none (docs only).

- [ ] **Step 1: Update the MainWindow UI paragraph**

In `CLAUDE.md`, in the `MainWindow.xaml`/`.cs` bullet that describes the format `ComboBox`es, add a sentence noting the Channels dropdown's first item is **"Auto (match source)"** (the `Config.Channels = 0` / `AudioFormats.AutoChannels` sentinel), the default for fresh configs; numeric picks remain hard requests.

- [ ] **Step 2: Update the Sender paragraph**

In the `SenderSession` description, add that `StartCapture` resolves Auto via `AudioFormats.ResolveCaptureChannels(config.Channels, nativeChannels)` where `nativeChannels` is the default render endpoint's `MixFormat.Channels` (endpoint configuration, **not** content — loopback only ever sees the post-mix stream), snapping down to the largest catalog count ≤ native and never up-mixing. Note it re-resolves on every `StartCapture`, so a device-change recovery follows the new device; and that the render endpoint is now acquired once (before `senderLock` on the first-start path) and passed to `LogCaptureFormat`.

- [ ] **Step 3: Update the Receiver paragraph**

In the `ReceiverSession` description, add that the receiver now reads the format code on **every** datagram and, on a valid change (a sender Auto re-resolve / device flip), rebuilds the whole pipeline via `BuildPipeline` (BufferedWaveProvider + underrun meter + `ReorderBuffer` + `WasapiOut`), cutting over at the live edge under `outputLock`; an unknown mid-stream code is ignored and logged once. Note the steady-state per-packet cost is a single code-compare (no alloc/lock).

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "Docs: describe Auto channel matching + receiver dynamic rebuild"
```

---

## Manual (user) verification — after all tasks

These require real audio devices and are run by the user (no test framework):

1. **Primary regression (the reported bug):** stereo system-audio source, select **Auto** on the sender. Sender logs `sending … 2ch`; receiver logs `Channels: 2`; playback level matches a native 2ch selection (no quiet up/down-mix).
2. **Explicit still forces:** select **6** on the same stereo source → sender still sends 6ch (hard request preserved).
3. **Dynamic rebuild:** while streaming Auto, change the sender's default render device (or sleep the HDMI monitor so it falls back to a stereo endpoint) → sender re-resolves, receiver logs `Wire format changed … rebuilding output.`, audio resumes at the new channel count without a restart.
4. **Receiver guard intact:** an unknown/foreign code is logged once (`Ignoring mid-stream datagram with unknown format code …`) with no rebuild storm.
5. **Config self-heal:** an existing `config.json` with `"Channels": 2` keeps stereo; deleting it regenerates with `"Channels": 0` (Auto).
