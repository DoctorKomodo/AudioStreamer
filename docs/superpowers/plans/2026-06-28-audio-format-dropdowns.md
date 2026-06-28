# Audio Format Dropdowns + Indexed Wire Code — Implementation Plan

> **For agentic workers:** implement task-by-task. Steps use checkbox (`- [ ]`) syntax. Spec: `docs/superpowers/specs/2026-06-28-audio-format-selection-design.md`.

**Goal:** Replace the sender's free-text Sample Rate / Bits Per Sample / Channels fields with selection-only dropdowns backed by a shared `AudioFormats` catalog, and change the wire format descriptor from byte-packed fields to a single index byte into that catalog.

**Architecture:** A new `AudioFormats` catalog is the single source of truth (allowed values + a 72-entry ordered table whose position is the wire code + code↔format helpers + UI labels). `WireProtocol` carries a 1-byte format code instead of three packed bytes. `SenderSession` writes the code; `ReceiverSession` decodes it and rejects out-of-range codes (the deferred item-16 guard). The WPF UI gets three `ComboBox`es populated from the catalog.

**Tech stack:** WPF/.NET 10 (`net10.0-windows`), NAudio (WASAPI), `System.Net.Sockets`.

## Global Constraints

- Flat namespace `AudioStreamer` for all files (no folder-matching namespaces).
- **No test project exists** (CLAUDE.md). Verification = `dotnet build` clean (0/0) + runtime smoke; the catalog round-trip is checked by reasoning + the end-to-end 44.1 kHz smoke. Do not scaffold a test framework.
- Work on branch `feature/audio-format-dropdowns` (already created). Do **not** push or merge.
- Commit messages end with the trailer:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- `WireProtocol` stays `internal`; `AudioFormats` is `public` (UI in the same assembly consumes it; no cross-assembly concern).
- This is a **breaking wire change** — both ends must run the same build (already required). `config.json` schema is unchanged (still stores the three ints).
- Allowed values (verbatim): sample rates `44100, 48000, 88200, 96000, 176400, 192000` (default `48000`); bit depths `16, 24, 32` (default `16`); channels `1, 2, 6, 8` (default `2`). 6×3×4 = 72 combinations.

---

## Task 1: `AudioFormats` catalog

**Files:**
- Create: `Streaming/AudioFormats.cs`

**Produces (consumed by Tasks 2–3):**
- `AudioFormats.SampleRates`/`BitDepths`/`ChannelCounts` (`int[]`)
- `AudioFormats.DefaultSampleRate`/`DefaultBitDepth`/`DefaultChannels` (`int` consts)
- `readonly record struct AudioFormats.Format(int SampleRate, int BitDepth, int Channels)`
- `IReadOnlyList<AudioFormats.Format> AudioFormats.Formats` (72 entries)
- `byte AudioFormats.ToCode(int sampleRate, int bitDepth, int channels)`
- `AudioFormats.Format? AudioFormats.FromCode(byte code)`
- `string AudioFormats.RateLabel(int)`, `DepthLabel(int)`, `ChannelLabel(int)`

- [ ] **Step 1: Create `Streaming/AudioFormats.cs`**

```csharp
using System.Globalization;

namespace AudioStreamer
{
    /// <summary>
    /// The catalog of selectable audio formats — single source of truth shared by the sender UI
    /// (dropdowns), SenderSession (format -> wire code), and ReceiverSession (wire code -> format +
    /// validity guard). The wire carries a 1-byte index into <see cref="Formats"/>; that ordering is
    /// part of the wire contract, so both ends must run the same build (already required — see WireProtocol).
    /// </summary>
    public static class AudioFormats
    {
        public static readonly int[] SampleRates   = { 44100, 48000, 88200, 96000, 176400, 192000 };
        public static readonly int[] BitDepths     = { 16, 24, 32 };
        public static readonly int[] ChannelCounts = { 1, 2, 6, 8 };

        public const int DefaultSampleRate = 48000;
        public const int DefaultBitDepth   = 16;
        public const int DefaultChannels   = 2;

        public readonly record struct Format(int SampleRate, int BitDepth, int Channels);

        /// <summary>
        /// Canonical ordered table: the cartesian product of the three arrays, sample-rate-major then
        /// bit-depth then channels. A format's position here IS its wire code, generated identically on
        /// both ends. 6 × 3 × 4 = 72 entries (fits one byte).
        /// </summary>
        public static readonly IReadOnlyList<Format> Formats = Build();

        private static Format[] Build()
        {
            var list = new List<Format>(SampleRates.Length * BitDepths.Length * ChannelCounts.Length);
            foreach (int rate in SampleRates)
                foreach (int depth in BitDepths)
                    foreach (int ch in ChannelCounts)
                        list.Add(new Format(rate, depth, ch));
            return list.ToArray();
        }

        /// <summary>
        /// Format -> wire code. Falls back to the default format's code (no throw) if the combo isn't in
        /// the table — a safety net for a hand-edited config.json; the dropdowns otherwise guarantee a
        /// valid combo.
        /// </summary>
        public static byte ToCode(int sampleRate, int bitDepth, int channels)
        {
            int idx = IndexOf(sampleRate, bitDepth, channels);
            if (idx < 0)
                idx = IndexOf(DefaultSampleRate, DefaultBitDepth, DefaultChannels);
            return (byte)idx;
        }

        /// <summary>Wire code -> format, or null if the code is out of range (the receiver guard).</summary>
        public static Format? FromCode(byte code) => code < Formats.Count ? Formats[code] : null;

        private static int IndexOf(int sampleRate, int bitDepth, int channels)
        {
            for (int i = 0; i < Formats.Count; i++)
            {
                Format f = Formats[i];
                if (f.SampleRate == sampleRate && f.BitDepth == bitDepth && f.Channels == channels)
                    return i;
            }
            return -1;
        }

        // Invariant culture so the decimal is always a dot ("44.1 kHz") regardless of the OS locale
        // (a comma-decimal locale would otherwise render "44,1 kHz"). The whole-kHz branch and DepthLabel
        // format small integers only (no separators), so they're locale-safe as-is.
        public static string RateLabel(int hz) =>
            hz % 1000 == 0
                ? $"{hz / 1000} kHz"
                : string.Create(CultureInfo.InvariantCulture, $"{hz / 1000.0:0.0} kHz");

        public static string DepthLabel(int bits) => $"{bits}-bit";

        public static string ChannelLabel(int ch) => ch switch
        {
            1 => "1 (Mono)",
            2 => "2 (Stereo)",
            6 => "6 (5.1)",
            8 => "8 (7.1)",
            _ => ch.ToString()
        };
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Verify the catalog by reasoning (no test framework)**

Confirm by inspection: `Formats` has `6*3*4 = 72` entries; `FromCode((byte)i)` for `i` in `0..71` returns the same `Format` that produced code `i` (so `FromCode(ToCode(r,d,c))` round-trips for every catalog combo); `FromCode(72)`..`FromCode(255)` return `null`; `ToCode(48000,16,2)` is in range (the default, code `13`). `RateLabel(44100) == "44.1 kHz"`, `RateLabel(48000) == "48 kHz"`. The end-to-end check happens in Task 2 Step 6.

- [ ] **Step 4: Commit**

```bash
git add Streaming/AudioFormats.cs
git commit -m "Add AudioFormats catalog (allowed values + indexed format table)"
```

---

## Task 2: Wire format code — `WireProtocol` + both sessions + receiver guard

This is one atomic task: the header-layout change breaks both sessions' compile until they're updated together, so they land as a unit. Implements followups item 16 (the receiver guard).

**Files:**
- Modify: `Streaming/WireProtocol.cs`
- Modify: `Streaming/SenderSession.cs` (`StartCapture`)
- Modify: `Streaming/ReceiverSession.cs` (`InitializeReceiver`)

**Interfaces:**
- Consumes: `AudioFormats.ToCode`, `AudioFormats.FromCode`, `AudioFormats.Format` (Task 1).
- Produces: `WireProtocol.WriteFormatCode(byte[] dest, byte code)`, `WireProtocol.ReadFormatCode(byte[] src) -> byte`; new constants `FormatHeaderBytes = 1`, `SequenceByteOffset = 1`, `HeaderBytes = 2`.

- [ ] **Step 1: Change the `WireProtocol` header layout**

In `Streaming/WireProtocol.cs`: update the class summary, the three offset constants, and replace `WriteFormatHeader`/`ReadFormatHeader` with code-byte accessors.

Replace the summary comment:
```csharp
    /// <summary>
    /// The on-the-wire UDP framing shared by sender and receiver. Every datagram is a 2-byte header
    /// (1-byte format code — an index into AudioFormats.Formats — plus a 1-byte wrapping sequence
    /// number) followed by raw PCM audio.
    /// </summary>
```

Replace the three constants:
```csharp
        public const int HeaderBytes = 2;          // total header (audio starts here on both sides)
        public const int FormatHeaderBytes = 1;    // byte 0: format code (index into AudioFormats.Formats)
        public const int SequenceByteOffset = 1;   // byte 1: wrapping per-datagram sequence number
```

Replace the two `WriteFormatHeader`/`ReadFormatHeader` methods (and their doc comments) with:
```csharp
        /// <summary>Writes the 1-byte format code into dest[0].</summary>
        public static void WriteFormatCode(byte[] dest, byte code) => dest[0] = code;

        /// <summary>Reads the 1-byte format code from src[0].</summary>
        public static byte ReadFormatCode(byte[] src) => src[0];
```

- [ ] **Step 2: Sender writes the format code (`SenderSession.StartCapture`)**

In `Streaming/SenderSession.cs`, replace the send-buffer/format-header lines (currently):
```csharp
                // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
                // dropout). The 3-byte format header is constant for the session, so write it once up front; the
                // sequence byte at index 3 is overwritten per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + maxChunk];
                WireProtocol.WriteFormatHeader(sendBuffer, config.SampleRate, config.BitsPerSample, config.Channels);
```
with:
```csharp
                // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
                // dropout). The 1-byte format code is constant for the session, so write it once up front; the
                // sequence byte at index 1 is overwritten per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + maxChunk];
                WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(config.SampleRate, config.BitsPerSample, config.Channels));
```
(The per-datagram `sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;` and `WireProtocol.HeaderBytes` usages downstream are unchanged — they already use the constants, which now resolve to the new offsets.)

- [ ] **Step 3: Receiver decodes the code + rejects unknown codes (`ReceiverSession.InitializeReceiver`)**

In `Streaming/ReceiverSession.cs`, add a log-once flag and replace the format-read + build block. The method currently reads:
```csharp
            Socket socket = this.socket!;
            logLine("Waiting for audio connection from sender");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received >= WireProtocol.FormatHeaderBytes)
                    {
                        var (sampleRate, bitDepth, channels) = WireProtocol.ReadFormatHeader(receiveBuffer);
                        logLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");
                        bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, bitDepth, channels))
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                            DiscardOnBufferOverflow = true
                        };
                        underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
                        BuildAndPlayOutput();
                        Task.Run(ReceiveAudio, token);
                        break;
                    }
                }
```
Replace the `Socket socket = this.socket!;` + loop header + the `if (received >= ...)` body with:
```csharp
            Socket socket = this.socket!;
            bool loggedBadFormat = false;
            logLine("Waiting for audio connection from sender");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received >= WireProtocol.FormatHeaderBytes)
                    {
                        byte code = WireProtocol.ReadFormatCode(receiveBuffer);
                        if (AudioFormats.FromCode(code) is not { } fmt)
                        {
                            // Unknown code: a foreign datagram on the port, or a version mismatch. Ignore it and
                            // keep waiting (log once so a stream of them doesn't flood the log). [followups item 16]
                            if (!loggedBadFormat)
                            {
                                logLine($"Ignoring datagram with unknown format code {code} (sender/receiver version mismatch?).");
                                loggedBadFormat = true;
                            }
                            continue;
                        }
                        logLine($"Sample rate: {fmt.SampleRate}, Bit depth: {fmt.BitDepth}, Channels: {fmt.Channels} received from sender");
                        bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels))
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                            DiscardOnBufferOverflow = true
                        };
                        underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
                        BuildAndPlayOutput();
                        Task.Run(ReceiveAudio, token);
                        break;
                    }
                }
```
(The `catch` block and the rest of the method are unchanged. `ReceiveAudio`'s `received > WireProtocol.HeaderBytes`, payload offset `WireProtocol.HeaderBytes`, and sequence read at `WireProtocol.SequenceByteOffset` are unchanged — they use the constants, now 2/2/1.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `0 Warning(s)`, `0 Error(s)`. (If the compiler reports `WriteFormatHeader`/`ReadFormatHeader` not found, a call site was missed — fix it to the new `WriteFormatCode`/`ReadFormatCode`.)

- [ ] **Step 5: Grep for stragglers**

Run: `git grep -n "ReadFormatHeader\|WriteFormatHeader" -- '*.cs'`
Expected: **no matches** (scoped to `.cs`; the historical plan/spec docs still mention the old names and are fine — Task 4 only updates CLAUDE.md + the followups doc).

- [ ] **Step 6: Runtime smoke — the 44.1 kHz regression check**

Two app instances on this machine (loopback): one Receiver on a port, one Sender to `127.0.0.1` on that port. (The sender captures whatever the default render device plays; any audio works.) Set the sender to **44100 Hz** — for now via `config.json` next to the sender exe (the dropdown lands in Task 3), or just confirm the default 48000 path end-to-end here and do the explicit 44.1k check after Task 3. Confirm `diagnostics.log` on the receiver shows `Sample rate: 44100, …` (NOT `44000`) and `[recv]` lines flow. This is the bug the indexed code fixes.

- [ ] **Step 7: Commit**

```bash
git add Streaming/WireProtocol.cs Streaming/SenderSession.cs Streaming/ReceiverSession.cs
git commit -m "Wire a 1-byte format code; reject unknown codes on the receiver (item 16)"
```

---

## Task 3: UI dropdowns

**Files:**
- Modify: `UI/MainWindow.xaml` (`SenderPanel`)
- Modify: `UI/MainWindow.xaml.cs` (`PopulateUIFromConfig`, `UpdateConfigFromUI`, + helpers)

**Interfaces:**
- Consumes: `AudioFormats.*` (Task 1).

- [ ] **Step 1: Swap the three `TextBox`es for `ComboBox`es in `UI/MainWindow.xaml`**

In `SenderPanel`, replace these three TextBlock+TextBox pairs:
```xml
                <TextBlock Text="Sample Rate" Margin="1"/>
                <TextBox x:Name="SampleRateTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Bits Per Sample" Margin="1"/>
                <TextBox x:Name="BitsPerSampleTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Channels" Margin="1"/>
                <TextBox x:Name="ChannelsTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>
```
with:
```xml
                <TextBlock Text="Sample Rate" Margin="1"/>
                <ComboBox x:Name="SampleRateCombo" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Bits Per Sample" Margin="1"/>
                <ComboBox x:Name="BitDepthCombo" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Channels" Margin="1"/>
                <ComboBox x:Name="ChannelsCombo" Width="200" Margin="1" HorizontalAlignment="Left"/>
```
(`ComboBox` is non-editable by default, so it's selection-only.)

- [ ] **Step 2: Add the option model + helpers in `UI/MainWindow.xaml.cs`**

Add a nested type and two helpers to the `MainWindow` class (e.g. just above `ParseOr`):
```csharp
        // Dropdown item: carries the int Config value, displays the friendly label (ToString is what the ComboBox renders).
        private sealed record FormatOption(int Value, string Label) { public override string ToString() => Label; }

        private void PopulateFormatDropdowns()
        {
            SampleRateCombo.ItemsSource = AudioFormats.SampleRates.Select(r => new FormatOption(r, AudioFormats.RateLabel(r))).ToList();
            BitDepthCombo.ItemsSource   = AudioFormats.BitDepths.Select(b => new FormatOption(b, AudioFormats.DepthLabel(b))).ToList();
            ChannelsCombo.ItemsSource   = AudioFormats.ChannelCounts.Select(c => new FormatOption(c, AudioFormats.ChannelLabel(c))).ToList();
        }

        private static void SelectValue(ComboBox combo, int value, int fallback)
        {
            var options = combo.ItemsSource.Cast<FormatOption>().ToList();
            combo.SelectedItem = options.FirstOrDefault(o => o.Value == value)
                              ?? options.FirstOrDefault(o => o.Value == fallback)
                              ?? options.First();   // fallback is always a catalog default, so this last leg never runs — belt-and-suspenders
        }

        private static int SelectedValue(ComboBox combo, int fallback) =>
            combo.SelectedItem is FormatOption o ? o.Value : fallback;
```

- [ ] **Step 3: Populate + preselect in `PopulateUIFromConfig`**

At the **top** of `PopulateUIFromConfig` (before any selection), add:
```csharp
            PopulateFormatDropdowns();
```
Then replace the three format `TextBox.Text` assignments:
```csharp
            SampleRateTextBox.Text = audioStreamerLogic.CurrentConfig.SampleRate.ToString();
            BitsPerSampleTextBox.Text = audioStreamerLogic.CurrentConfig.BitsPerSample.ToString();
            ChannelsTextBox.Text = audioStreamerLogic.CurrentConfig.Channels.ToString();
```
with:
```csharp
            SelectValue(SampleRateCombo, audioStreamerLogic.CurrentConfig.SampleRate, AudioFormats.DefaultSampleRate);
            SelectValue(BitDepthCombo, audioStreamerLogic.CurrentConfig.BitsPerSample, AudioFormats.DefaultBitDepth);
            SelectValue(ChannelsCombo, audioStreamerLogic.CurrentConfig.Channels, AudioFormats.DefaultChannels);
```

- [ ] **Step 4: Read the dropdowns in `UpdateConfigFromUI`**

Replace the three `ParseOr` format lines:
```csharp
            cfg.SampleRate = ParseOr(SampleRateTextBox.Text, cfg.SampleRate);
            cfg.BitsPerSample = ParseOr(BitsPerSampleTextBox.Text, cfg.BitsPerSample);
            cfg.Channels = ParseOr(ChannelsTextBox.Text, cfg.Channels);
```
with:
```csharp
            cfg.SampleRate = SelectedValue(SampleRateCombo, cfg.SampleRate);
            cfg.BitsPerSample = SelectedValue(BitDepthCombo, cfg.BitsPerSample);
            cfg.Channels = SelectedValue(ChannelsCombo, cfg.Channels);
```
(The buffer/latency/port `ParseOr` lines and `ParseOr` itself stay.)

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Runtime smoke**

Launch the app (Sender mode). Confirm: the three dropdowns show friendly labels ("44.1 kHz", "16-bit", "2 (Stereo)", …); the selections match `config.json` (default 48 kHz / 16-bit / Stereo on a fresh config); changing a selection and clicking Start persists it (reopen → same selection). Then run the Task 2 Step 6 loopback with the sender set to **44.1 kHz via the dropdown** and confirm the receiver logs `Sample rate: 44100`.

- [ ] **Step 7: Commit**

```bash
git add UI/MainWindow.xaml UI/MainWindow.xaml.cs
git commit -m "Replace sender format TextBoxes with catalog-backed dropdowns"
```

---

## Task 4: Documentation

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/2026-06-27-streaming-review-followups.md`

- [ ] **Step 1: Update `CLAUDE.md`**

- Wire-protocol paragraph: change the "4-byte header" description to the new layout — byte 0 is a 1-byte **format code** (index into `AudioFormats.Formats`, the 72-entry catalog), byte 1 the wrapping sequence, audio at offset 2 (`HeaderBytes` 2). Note `WriteFormatCode`/`ReadFormatCode` replace `Write/ReadFormatHeader`, and that the catalog order is part of the wire contract (same-version-both-ends, already required). Mention `AudioFormats` as the shared catalog (sender UI + sender code + receiver guard).
- MainWindow UI paragraph: note the three sender format fields (Sample Rate, Bits Per Sample, Channels) are now selection-only `ComboBox`es populated from `AudioFormats`, not free-text `TextBox`es.

- [ ] **Step 2: Update the followups doc**

In `docs/2026-06-27-streaming-review-followups.md`:
- Mark **item 16** `**[DONE]**`: the receiver now decodes a 1-byte catalog code and rejects out-of-range codes (`AudioFormats.FromCode(code) is null`), which subsumes the proposed multi-field check; note its original fractional-kHz "known set" was the inconsistency, resolved by the indexed catalog (44.1 kHz now round-trips exactly).
- Update the **Status** line so 16 is no longer pending (15 and 17 remain).
- Update the **Wire protocol (current)** section to the new layout: byte 0 = format code (index into `AudioFormats.Formats`), byte 1 = sequence, byte 2+ = PCM.

- [ ] **Step 3: Build + commit**

Run: `dotnet build` (docs-only, but confirm nothing else is dirty) → `0 Warning(s) 0 Error(s)`.
```bash
git add CLAUDE.md docs/2026-06-27-streaming-review-followups.md
git commit -m "Document indexed wire format code + format dropdowns; close item 16"
```

---

## Verification summary

| Task | Verification |
|------|--------------|
| 1 AudioFormats | build 0/0 + catalog reasoning (72 entries, round-trip, out-of-range → null) |
| 2 Wire code + sessions + guard | build 0/0 + grep clean + loopback smoke (44.1 kHz logs 44100) |
| 3 UI dropdowns | build 0/0 + launch (labels, preselect, persist) + 44.1 kHz via dropdown end-to-end |
| 4 Docs | build 0/0 |

No automated tests exist; correctness rests on build + runtime smoke, with the 44.1 kHz loopback as the end-to-end regression check for the bug this closes. `config.json` schema is unchanged, so existing configs load and preselect normally.

## Out of scope

- Receiver-mode UI (receiver derives format from the wire); buffer/latency/port stay free-text.
- Pruning extreme combos (all 72 are valid, independently selectable).
- Followups items 15 (log-flood suppression) and 17 (sender `IPAddress.Parse` leak) — unrelated, stay pending.
