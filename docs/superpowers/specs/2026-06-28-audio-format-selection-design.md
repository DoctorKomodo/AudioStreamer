# Audio Format Selection (Dropdowns + Indexed Wire Code) — Design

Date: 2026-06-28
Status: design (awaiting review → plan)

## Goal

Replace the sender's three free-text format fields (Sample Rate, Bits Per Sample, Channels)
with selection-only dropdowns, and change the wire format descriptor from byte-packed fields
to a **single index into a shared format catalog**. This makes invalid/unrepresentable audio
parameters impossible to choose *and* impossible to put on the wire, and implements the
deferred receiver-side guard (followups item 16) as a trivial range check.

## Problem

1. **Free-text entry allows invalid values.** The UI exposes `SampleRate`/`BitsPerSample`/
   `Channels` as `TextBox`es parsed with `ParseOr`. Nothing constrains them to values the app
   (or the wire) can actually carry.
2. **The wire header silently truncates the sample rate.** `WireProtocol.WriteFormatHeader`
   stores `sampleRate / 1000` in one byte; `ReadFormatHeader` multiplies back by 1000. So only
   whole-kHz rates ≤ 255 kHz round-trip. `44100` → byte `44` → decoded `44000` Hz — wrong
   pitch/speed, with no error. This is why supporting 44.1 kHz requires a wire change.
3. **Followups item 16 is internally inconsistent.** Its proposed receiver "known set" lists
   fractional-kHz rates (11.025, 22.05, 44.1, 88.2, 176.4) that the current byte header cannot
   represent. The guard would "accept" rates the wire corrupts.

## Locked decisions

- **Allowed values** (the catalog):
  - Sample rates: `44100, 48000, 88200, 96000, 176400, 192000` (default `48000`)
  - Bit depths: `16, 24, 32` (default `16`)
  - Channels: `1, 2, 6, 8` (default `2`)
  - = **6 × 3 × 4 = 72** combinations.
- **Wire encoding = B: a single 1-byte index** into a deterministically-ordered catalog table
  (72 entries → fits one byte with large headroom). Chosen over byte-packed fields because it
  is the most compact (format descriptor shrinks 3 bytes → 1), makes invalid combinations
  unrepresentable on the wire, and reduces the receiver guard to one range check.

## Architecture / components

### 1. `Streaming/AudioFormats.cs` (new) — the shared catalog (single source of truth)

The semantic mapping between an audio format and its wire code. Consumed by the sender UI
(populating dropdowns), `SenderSession` (format → code), and `ReceiverSession` (code → format,
plus the validity guard).

```csharp
public static class AudioFormats
{
    public static readonly int[] SampleRates  = { 44100, 48000, 88200, 96000, 176400, 192000 };
    public static readonly int[] BitDepths    = { 16, 24, 32 };
    public static readonly int[] ChannelCounts = { 1, 2, 6, 8 };

    public const int DefaultSampleRate = 48000;
    public const int DefaultBitDepth   = 16;
    public const int DefaultChannels   = 2;

    public readonly record struct Format(int SampleRate, int BitDepth, int Channels);

    // Canonical ordered table: the cartesian product of the three arrays in a fixed order
    // (sample-rate-major, then bit-depth, then channels). The position in this list IS the wire
    // code. Generated deterministically, so both ends produce an identical table.
    public static readonly IReadOnlyList<Format> Formats; // 72 entries

    // Sender: (rate, depth, channels) -> wire code. Returns the code, or the DEFAULT format's
    // code (with no throw) if the combo isn't in the table — a safety net for a hand-edited
    // config.json; the dropdowns otherwise guarantee a valid combo.
    public static byte ToCode(int sampleRate, int bitDepth, int channels);

    // Receiver: wire code -> format, or null if the code is out of range (the item-16 guard).
    public static Format? FromCode(byte code);

    // UI labels.
    public static string RateLabel(int hz);     // "44100 Hz", "48000 Hz", ... (raw Hz, matches Windows; locale-safe)
    public static string DepthLabel(int bits);  // "16-bit"
    public static string ChannelLabel(int ch);  // "1 (Mono)", "2 (Stereo)", "6 (5.1)", "8 (7.1)"
}
```

**Wire-contract note:** the code↔format mapping is defined by this table's order. Changing the
arrays (adding/removing/reordering a rate/depth/channel) changes the mapping and is therefore a
**wire-protocol change** — both ends must run the same version, which `CLAUDE.md` already
requires. New combinations should be **appended** (extend the arrays) rather than reordered, to
minimise churn.

### 2. `Streaming/WireProtocol.cs` — header layout change

The format descriptor becomes a single byte (the catalog code). New datagram layout:

| offset | bytes | meaning |
|--------|-------|---------|
| 0 | 1 | format code (index into `AudioFormats.Formats`) |
| 1 | 1 | wrapping sequence number |
| 2+ | … | raw PCM audio |

Constant changes: `FormatHeaderBytes` 3 → **1**, `SequenceByteOffset` 3 → **1**, `HeaderBytes`
4 → **2**. `MaxUdpAudioBytes` (1440) unchanged; datagram is now 1442 + 28 (IP/UDP) = 1470 ≤ 1500.

`WriteFormatHeader`/`ReadFormatHeader` are replaced by thin code-byte accessors (WireProtocol
stays pure framing; it does not know the catalog):

```csharp
public static void WriteFormatCode(byte[] dest, byte code); // dest[0] = code
public static byte ReadFormatCode(byte[] src);              // src[0]
```

Both sessions already reference the named constants, so the offset shift flows through with
minimal edits.

### 3. UI — `UI/MainWindow.xaml` + `MainWindow.xaml.cs`

In `SenderPanel`, replace the three `TextBox`es (`SampleRateTextBox`, `BitsPerSampleTextBox`,
`ChannelsTextBox`) with non-editable `ComboBox`es (`SampleRateCombo`, `BitDepthCombo`,
`ChannelsCombo`), same 200-width layout.

- **Population:** filled in code-behind from `AudioFormats.SampleRates`/`BitDepths`/
  `ChannelCounts`, each item showing the friendly label with the underlying `int` as its value
  (e.g. an item model `{ int Value; string Label; }`, or `Tag`-carried int). One source of
  truth — the lists are not duplicated in XAML.
- **`UpdateConfigFromUI`:** read the selected item's `int` value for the three fields (no
  `ParseOr` — selection-only is always valid). The buffer/latency/port `TextBox`es keep
  `ParseOr` unchanged.
- **`PopulateUIFromConfig`:** select the item whose value equals the stored config value; if no
  item matches (old/hand-edited config), select the default value's item. On the next
  Start/close, `UpdateConfigFromUI` writes the normalised value back, so config self-heals.

`Config` schema is **unchanged** — it still stores `SampleRate`/`BitsPerSample`/`Channels` as
ints (human-readable `config.json`). The wire code is derived from those at capture start; it is
not persisted.

### 4. Receiver guard — `Streaming/ReceiverSession.cs` (implements item 16)

In `InitializeReceiver`, after reading the code:

```csharp
byte code = WireProtocol.ReadFormatCode(receiveBuffer);
if (AudioFormats.FromCode(code) is not { } fmt)
{
    // log once (don't flood), keep waiting — ignore this datagram (item 15's first-packet logging applies)
    continue;
}
var waveFormat = new WaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels);
```

The first-packet length check changes from `>= FormatHeaderBytes` (now 1) — i.e. any datagram
with at least the code byte — and an out-of-range code is rejected instead of building a bogus
`WaveFormat`. This replaces item 16's multi-field sanity check with a single membership test.

### 5. Sender — `Streaming/SenderSession.cs`

In `StartCapture`, compute the code once and write it into the reused send buffer header:

```csharp
byte formatCode = AudioFormats.ToCode(config.SampleRate, config.BitsPerSample, config.Channels);
WireProtocol.WriteFormatCode(sendBuffer, formatCode);   // offset 0, written once per (re)build
// per datagram: sendBuffer[WireProtocol.SequenceByteOffset] = sequence++  (now offset 1)
```

`LogCaptureFormat` already logs the human-readable format, so diagnostics stay readable even
though the wire is now an opaque code.

## Data flow

```
Sender config (rate, depth, ch)
  → AudioFormats.ToCode → 1-byte code → datagram[0]
                                          datagram[1] = sequence
                                          datagram[2+] = PCM
Receiver datagram[0] → WireProtocol.ReadFormatCode → AudioFormats.FromCode
  → valid?  yes → WaveFormat(rate, depth, ch) → build pipeline
            no  → log once, keep waiting (guard)
```

## Error handling / edge cases

- **Hand-edited `config.json` with an off-catalog combo:** `PopulateUIFromConfig` clamps the
  dropdowns to the default; `UpdateConfigFromUI` (always called before `Start`, including the
  `StartMinimized` auto-start path) normalises config before the sender reads it. As a final
  safety net, `AudioFormats.ToCode` returns the default format's code rather than throwing.
- **Device rejects a requested format** (e.g. exotic depth): `StartRecording` throws as today →
  surfaced as a friendly start failure. Unchanged.
- **24-bit / 32-bit capture/playback:** WASAPI shared-mode + `AutoConvertPcm` converts the
  device's native mix format to the requested PCM format on both ends; both build identical
  `WaveFormat`s from the same code, so there's no int/float ambiguity.
- **Stray/foreign datagram on the port:** an out-of-range code is now rejected by the guard
  (previously a plausible byte triple could build a wrong pipeline).

## Backward / cross-version compatibility

This is a **breaking wire change** (format descriptor reinterpreted; offsets shift). Old and new
builds cannot interoperate — acceptable under the existing "both sides must run the same version"
rule (`CLAUDE.md`, `WireProtocol`). `config.json` is **not** broken: its schema is unchanged.

## Out of scope

- Receiver-mode UI (receiver derives format from the wire, as today; its format fields stay
  hidden).
- The buffer/latency/port fields remain free-text `TextBox`es.
- Pruning "extreme" combos (e.g. 192 kHz/32-bit/8ch) — all 72 are valid PCM and independently
  selectable; we don't second-guess the user.

## Verification

No test project exists. Plan-time verification:
- Build clean (0/0).
- Runtime smoke: launch, confirm the three dropdowns populate with friendly labels and the
  stored config preselects; run a Sender↔Receiver loop and confirm the format flows and audio
  plays.
- **Key regression check** (the bug this fixes): select **44.1 kHz** on the sender and confirm
  the receiver logs `Sample rate: 44100` (not `44000`) and plays at correct pitch.
- A throwaway console check (optional, no framework) can assert `Formats.Count == 72` and that
  `FromCode(ToCode(r,d,c)) == (r,d,c)` round-trips for every catalog combo, plus `FromCode` of an
  out-of-range byte returns null.

## Docs to update (part of the work)

- `CLAUDE.md`: the wire-protocol paragraph (1-byte format code + `AudioFormats` table; new
  offsets) and the MainWindow UI paragraph (format dropdowns).
- `docs/2026-06-27-streaming-review-followups.md`: mark item 16 **[DONE]** (note it's subsumed by
  the indexed code + range check; its fractional-kHz set was the inconsistency), and update the
  "Wire protocol (current)" section to the new layout.
