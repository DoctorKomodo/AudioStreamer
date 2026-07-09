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

AudioStreamer keeps its own gradient, measured from the current icon:
`#368AFB` (top) → `#175BCC` (bottom).

## The Glyph

**Full** — 48, 64, 128, 256px: white speaker (trapezoid + rectangle) plus three
thin concentric wave arcs, as today.

**Simplified** — 16, 32px: the same speaker, plus **two** arcs. The outermost arc
is dropped and the remaining two are thickened so each lands on a whole device
pixel at 16px. All coordinates snapped to the pixel grid.

Small frames are redrawn, never downscaled from a larger frame.

Expected result at 16px: **3 bright masses** (speaker + 2 arcs), against 4
fragmented masses today.

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
2. The 16px frame contains at least N near-white pixels (luminance > 0.95),
   where N is **calibrated by measurement** against the finished artwork, not
   copied from AudioLeash. Today's icon scores 11.
3. The 16px frame's bright pixels (luminance > 0.80) form exactly **3**
   connected masses — the speaker and two arcs. Today's icon scores 4.

Assertions 2 and 3 must both be **observed failing** against deliberately broken
artwork (regenerate with the simplification disabled) before they are trusted. A
guard nobody has watched fail is not yet a guard.

Note that assertions 2 and 3 fail against the *current* icon. That is the point:
they encode the fix.

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
