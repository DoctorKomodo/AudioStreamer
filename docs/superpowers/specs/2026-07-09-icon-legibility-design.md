# AudioStreamer Icon Legibility

**Date:** 2026-07-09
**Status:** Approved

Ports the icon system built for AudioLeash
(`C:\Users\Andreas\Repos\AudioLeash`, PR #15) to AudioStreamer.

## Problem

Two distinct defects, both invisible until measured.

**1. The small frames fragment.** The glyph is a speaker plus three thin wave
arcs. At 16x16 — the size Windows renders in the notification area — the arcs
fall below one pixel and scatter. Measured on the current `Resources/icon.ico`:

| Metric (16px frame) | AudioStreamer today | AudioLeash (redesigned) |
|---|---|---|
| Distinct bright masses | 4 (fragmented) | 2 |
| Near-white pixels (lum > 0.95) | 11 | 27 |

The speaker survives; the arcs dissolve into grey specks.

**2. The tray never displays the 16px frame anyway.** `UI/MainWindow.xaml.cs:39`
calls `new System.Drawing.Icon(stream)`, which selects the `.ico`'s *default*
frame (32x32) and leaves `NotifyIcon` to downscale it. This is the same defect
found and fixed in AudioLeash. **It must be verified empirically on this repo
before the loader change is claimed to work.** If `new Icon(stream)` here
already returns 16x16, the loader change is reverted and only the artwork ships.

## Goal

An icon that stays crisp at 16x16 in the notification area, reproducible from a
committed generator, guarded by tests — matching AudioLeash's icon system so the
two remain a visible family.

## What Ports Unchanged

The tile geometry is already shared: AudioLeash's tile was *measured from this
icon*. Corner radius 15.3% of tile width, vertical gradient, white glyph at ~54%
of tile width, rounded rect on transparency.

Ported verbatim from `AudioLeash/tools/generate-icon.py`:

- 8x supersampling, LANCZOS reduction
- device-pixel snapping for the small frames
- the `append_images` ICO writer (Pillow matches frames by exact size and embeds
  them verbatim; only unmatched sizes get `thumbnail()`-downscaled)
- six frames: 16, 32, 48, 64, 128, 256

AudioStreamer keeps its own gradient, measured from the current icon's 256px
frame at the tile's extreme rows (y=1 and y=255 at x=128):
`#388EFF` (top) → `#1457C8` (bottom).

(An earlier draft recorded `#368AFB → #175BCC`. Those were sample points inset
from the tile's edges, not its endpoints. AudioLeash's own spec repeats the same
mistaken values in its "Family Template" table; harmless there, since AudioLeash
uses its own cyan gradient, but worth correcting if that doc is ever revised.)

## The Glyph

**Full** — 48, 64, 128, 256px: white speaker (trapezoid + rectangle) plus three
thin concentric wave arcs, as today.

**Simplified** — 16, 32px: the same speaker, plus **two** arcs. The outermost arc
is dropped and the remaining two are thickened. All coordinates snapped to the
pixel grid.

Small frames are redrawn, never downscaled from a larger frame.

### The geometry is not free — it was found by measurement

A first pass at this glyph failed. Arcs thin enough to stay separate fragment
into specks; arcs thick enough to stay whole merge into each other or into the
speaker. Prototyping the 16px frame directly (all values as fractions of tile
width, arc centre at x=0.25, y=0.5, sweep ±62°):

| Arc radii | Stroke | Masses | Near-white | Verdict |
|---|---|---|---|---|
| 0.1875 / 0.3125 | 1px | 4 (sizes 26,4,3,3) | 24 | arcs shatter |
| 0.25 / 0.375 | 1px | 7 | 27 | arcs shatter |
| 0.4375 / 0.6875 | 1.5px | 3 | 47 | arcs graze the speaker |
| **0.5 / 0.75** | **1.5px** | **3 (sizes 25,23,21)** | **47** | **chosen** |
| 0.5 | 1.5px | 2 | 32 | one arc; loses the "streaming" read |
| 0.5 / 0.75 | 2px | 1 | — | arcs merge into speaker |

The chosen geometry yields three substantial masses with no speck, and nearly
doubles the near-white pixel count against the icon we are replacing (47 vs 11).

### Connectivity: 8, not 4

**This is the load-bearing detail.** A 1px-wide *curved* stroke is 8-connected
but not 4-connected: each diagonal step breaks the 4-neighbourhood. Under
4-connectivity even the chosen geometry reports 4 masses, because the outer arc
splits at its diagonal steps. Under 8-connectivity it reports exactly 3.

AudioLeash's guard uses 4-connectivity, which is correct *there* because its
second shape is a closed ring — a single component either way. That choice does
not port. AudioStreamer's guard must use **8-connectivity**, or it will fail on
correct artwork.

## Full Regeneration

All six frames are generated parametrically. The 256px frame will **not** be
byte-identical to today's — it is a redraw in the same visual language, not a
copy. This was chosen deliberately over preserving the existing large frames, to
keep a single reproducible source of truth.

The arcs are the most delicate part of the existing artwork. If the redrawn
256px frame looks worse than the original side by side, the response is to
**tune the arc geometry until it does not** — not to fall back to the old
frames. The comparison is made by rendering both and looking at them.

## Source of Truth

A committed `tools/generate-icon.py`, structured so the shared machinery is
clearly separated from an app-specific config block (gradient, `FULL`, `SIMPLE`).

This duplicates ~120 lines with AudioLeash's copy. That cost was accepted
knowingly, in exchange for each repo remaining independently buildable with no
submodule or package infrastructure. Each file carries a header comment naming
its sibling, so a future fix to one is likely to reach the other. **The drift
risk is real and is not mitigated further.**

Pillow is a dev-time-only dependency, invoked manually. It is not added to any
`.csproj` and is not wired into the build.

The single `.ico` continues to serve all three consumers, so neither the
`.csproj` nor the `.iss` needs changing:

- `AudioStreamer.csproj`: `<ApplicationIcon>` and `<EmbeddedResource>`
- `installer/AudioStreamer.iss`: `SetupIconFile=..\Resources\icon.ico`
- `UI/MainWindow.xaml.cs`: embedded resource stream, via the new loader

Neither `MainWindow.xaml` nor `App.xaml` sets an `Icon=` attribute, so the WPF
title-bar and taskbar icon derives from `<ApplicationIcon>` and updates for free.
Verified, not assumed.

The current icon already ships a PNG-encoded 256px frame through this same Inno
Setup 6 `SetupIconFile`, so PNG frames are a proven non-issue for the installer.

## Code Change

Extract the icon load out of `MainWindow.SetupTrayIcon` into a testable
`TrayIconLoader`:

```csharp
internal static class TrayIconLoader
{
    internal static Icon Load(Size desired);
}
```

`internal`, not `public`: `AudioStreamer.csproj` already declares
`<InternalsVisibleTo Include="AudioStreamer.Tests" />`, and this matches the
repo's existing style. (AudioLeash's equivalent is `public` because that project
has no `InternalsVisibleTo`.)

`Load` resolves the embedded resource by suffix match (preserving the current
`GetManifestResourceNames` lookup, which does not hardcode the resource name),
and returns `new Icon(stream, desired)`. Its fallback returns
`(Icon)SystemIcons.Application.Clone()` rather than the shared static, so a
caller that disposes the result cannot destroy the process-wide instance.

`SetupTrayIcon` then calls `TrayIconLoader.Load(SystemInformation.SmallIconSize)`.

**Out of scope:** `MainWindow.ExitApp` disposes the `NotifyIcon`
(`trayIcon?.Dispose()`) but never the `Icon` assigned to it — disposing a
`NotifyIcon` does not dispose its `Icon`. That GDI handle leaks for the process
lifetime. It is pre-existing, unchanged by this work, and not fixed here. Note
the fallback clone does not make it worse: today's fallback returns the shared
`SystemIcons.Application`, which is likewise never disposed.

## Verification

**Automated** (`AudioStreamer.Tests`, reading the embedded resource):

1. The `.ico` carries all six sizes: 16, 32, 48, 64, 128, 256.
2. The 16px frame contains at least N near-white pixels (luminance > 0.95).
   The prototype scores 47 and today's icon scores 11, so N is set to **30** —
   comfortably clear of both. Recalibrate against the finished artwork if it
   drifts, but never downward to make a red test green.
3. The 16px frame's bright pixels (luminance > 0.80) form exactly **3**
   **8-connected** masses — the speaker and two arcs. Today's icon scores 4.
4. **No bright component is smaller than 8 pixels.** This is what actually
   catches fragmentation. An exact mass count alone is brittle: three masses
   plus a stray speck would still be "4", and two arcs that merged would be "2",
   but a count that happens to land on 3 while one arc has crumbled into a
   3-pixel fleck would pass. The prototype's components are 25/23/21.

Assertions 2, 3 and 4 must be **observed failing** against deliberately broken
artwork (regenerate with the simplification disabled) before they are trusted. A
guard nobody has watched fail is not yet a guard.

Assertions 2–4 fail against the *current* icon. That is the point: they encode
the fix. **Sequence the commits so the new `.ico` and the new tests land
together**, or the tree is red in between.

The ported test must load `"AudioStreamer.Resources.icon.ico"`, not AudioLeash's
resource name. `RootNamespace` is the project name, so that is the resource ID —
verify with `GetManifestResourceNames()` rather than assuming.

Tests read the `.ico` only, need no audio stack or network, and decode PNG frame
payloads directly rather than via `Icon.ToBitmap()`, which is unreliable for
PNG-compressed frames. The test project targets `net10.0-windows` and the app
references WinForms, so `System.Drawing` resolves.

**Manual:**

- Render every frame; view the 16px zoomed.
- Compare the new 256px frame against the original, side by side.
- Launch AudioStreamer and screenshot the real notification area.

No claim about the icon's appearance is made without having looked at the
rendered output.

## Out of Scope

- Per-DPI frame selection (no 20/24px frames). At 150% scaling Windows renders
  the 32px frame at 24px. Never worse than today.
- Light/dark tray variants — the tile carries its own background.
- The `Icon` disposal leak in `MainWindow`.
- Changing AudioLeash's icon.

## Documentation

`README.md` is updated only if user-facing behaviour changes. An icon's
appearance changing is not a behaviour change, so README is expected to stay
untouched — confirm by grepping it for icon references rather than assuming.

`CLAUDE.md` carries no project-structure tree, only prose and command blocks, so
there is no tree to update. It gains a short **Regenerating the icon** block in
the same style as its existing Build and Installer blocks, covering the Pillow
prerequisite, `python tools/generate-icon.py`, the redrawn-not-downscaled rule
for the small frames, and the fact that `IconAssetTests` enforces it.

This repo has no `DEVELOPMENT.md`; do not create one.
