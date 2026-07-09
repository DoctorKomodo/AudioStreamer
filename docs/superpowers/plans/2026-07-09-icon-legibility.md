# Icon Legibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make AudioStreamer's tray icon crisp at 16x16, generated from a committed script and guarded by tests, matching the icon system already shipped in AudioLeash.

**Architecture:** A committed `tools/generate-icon.py` (Python + Pillow, dev-time only) renders six frames — 16, 32, 48, 64, 128, 256 — into one `.ico`. The 16px and 32px frames use *redrawn, simplified* geometry (speaker + two thick arcs), never a downscale. On the C# side, icon loading moves out of `MainWindow.SetupTrayIcon` into a testable `internal static TrayIconLoader` that selects the frame matching a requested size.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF + WinForms `NotifyIcon`, xUnit, `System.Drawing` (tests decode frames); Python 3 + Pillow (dev-time only).

**Spec:** `docs/superpowers/specs/2026-07-09-icon-legibility-design.md`

## Global Constraints

- Target framework: app and tests both `net10.0-windows`. Windows only.
- `<Nullable>enable</Nullable>` in both projects — all new code null-safe.
- Dispose all `IDisposable` — `Icon`, `Bitmap`, `MemoryStream`.
- Pillow is a **dev-time-only** dependency. Do not add it to any `.csproj`; do not wire the generator into the build.
- Do **not** modify `AudioStreamer.csproj` or `installer/AudioStreamer.iss`. The single `Resources/icon.ico` serves `<ApplicationIcon>`, `<EmbeddedResource>`, and the installer's `SetupIconFile`.
- `.ico` must contain exactly these six sizes: 16, 32, 48, 64, 128, 256.
- Tile geometry, verbatim from the spec: corner radius `0.153` of tile width; gradient `#388EFF` (top) → `#1457C8` (bottom); glyph pure white `#FFFFFF`.
- Simplified-glyph geometry, verbatim from the spec (fractions of tile width): arc centre `(0.25, 0.5)`, arc radii `0.5` and `0.75`, arc stroke `0.09375` (= 1.5px at 16px), sweep `±62°`.
- Bright-mass counting uses **8-connectivity**, not 4. A 1px curved stroke is 8-connected but not 4-connected.
- The embedded resource is `AudioStreamer.Resources.icon.ico` — **not** AudioLeash's name.
- Existing baseline: `dotnet test AudioStreamer.Tests` passes with **23 tests**.
- Conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`). Work stays on branch `feature/icon-legibility`.
- Namespace style in this repo is brace-scoped (`namespace AudioStreamer { ... }`), not file-scoped. Match it.

## Commit Ordering

The new tests fail against the *current* icon by design. Land the generator and
the new `.ico` (Task 2) **before** the tests (Task 3). Never the reverse — that
would leave the tree red between commits. This satisfies the spec's sequencing
requirement.

---

### Task 1: Extract `TrayIconLoader` and select the small frame

Independent of the artwork. `new Icon(stream)` selects the `.ico`'s default 32x32 frame and lets `NotifyIcon` downscale it to tray size.

**Premise gate.** Step 2 verifies this empirically. If it turns out `new Icon(stream)` already returns 16x16 on this repo, **stop, report BLOCKED, and do not implement `TrayIconLoader`.** Do not weaken an assertion to accommodate the result. Shipping nothing beats shipping a change that does nothing.

**Files:**
- Create: `UI/TrayIconLoader.cs`
- Modify: `UI/MainWindow.xaml.cs` (`SetupTrayIcon`, lines ~33-54)
- Test: `AudioStreamer.Tests/TrayIconLoaderTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal static class AudioStreamer.TrayIconLoader` with `internal static Icon Load(Size desired)`. Task 3 uses its assembly to reach the embedded resource.

- [ ] **Step 1: Write the failing test**

Create `AudioStreamer.Tests/TrayIconLoaderTests.cs`:

```csharp
using System.Drawing;
using Xunit;

namespace AudioStreamer.Tests
{
    public class TrayIconLoaderTests
    {
        [Theory]
        [InlineData(16)]
        [InlineData(32)]
        [InlineData(48)]
        public void Load_SelectsTheFrameMatchingTheRequestedSize(int px)
        {
            using var icon = TrayIconLoader.Load(new Size(px, px));

            Assert.Equal(new Size(px, px), icon.Size);
        }

        /// <summary>
        /// Documents why TrayIconLoader exists: the parameterless Icon(Stream)
        /// constructor selects the .ico's default frame (32x32), not the 16x16
        /// frame the tray needs. NotifyIcon then downscales it, blurring the
        /// pixel-tuned small artwork.
        /// </summary>
        [Fact]
        public void DefaultIconConstructor_DoesNotSelectTheSmallestFrame()
        {
            var asm = typeof(TrayIconLoader).Assembly;
            string resName = System.Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase))!;

            using var stream = asm.GetManifestResourceStream(resName)!;
            using var icon = new Icon(stream);

            Assert.NotEqual(new Size(16, 16), icon.Size);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails, and check the premise**

Run: `dotnet test AudioStreamer.Tests --filter FullyQualifiedName~TrayIconLoaderTests`

Expected: **compile error**, `CS0103: The name 'TrayIconLoader' does not exist`.

Now verify the premise. Temporarily comment out the `[Theory]` and change `typeof(TrayIconLoader)` to `typeof(MainWindow)`, then run only `DefaultIconConstructor_DoesNotSelectTheSmallestFrame`.

Expected: **PASS**, confirming `new Icon(stream).Size` is not 16x16.

If it **fails**, the premise is wrong: stop and report BLOCKED. Otherwise restore the file and continue.

- [ ] **Step 3: Write the implementation**

Create `UI/TrayIconLoader.cs`:

```csharp
using System.Drawing;
using System.Reflection;

namespace AudioStreamer
{
    /// <summary>
    /// Loads the embedded tray icon at a requested frame size.
    /// </summary>
    internal static class TrayIconLoader
    {
        /// <summary>
        /// Loads the icon frame matching <paramref name="desired"/>. The caller
        /// owns the returned <see cref="Icon"/> on both paths and must dispose it.
        /// </summary>
        /// <remarks>
        /// The parameterless <c>Icon(Stream)</c> constructor selects the .ico's
        /// default frame — 32x32 — leaving NotifyIcon to downscale it to tray
        /// size, which blurs the hand-tuned 16px artwork. Passing the desired
        /// size makes GDI+ pick the matching frame instead.
        /// </remarks>
        internal static Icon Load(Size desired)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string? resName = System.Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase));

                if (resName is not null)
                {
                    using Stream? stream = asm.GetManifestResourceStream(resName);
                    if (stream is not null)
                        return new Icon(stream, desired);
                }
            }
            catch { /* fall through to the system default */ }

            // A clone, not the shared static: the caller owns and disposes the result.
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test AudioStreamer.Tests --filter FullyQualifiedName~TrayIconLoaderTests`

Expected: PASS, 4 tests.

- [ ] **Step 5: Wire it into `MainWindow`**

In `UI/MainWindow.xaml.cs`, replace the head of `SetupTrayIcon`:

```csharp
// before
var asm = System.Reflection.Assembly.GetExecutingAssembly();
string? resName = System.Array.Find(asm.GetManifestResourceNames(),
    n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase));
System.Drawing.Icon icon = resName is not null
    ? new System.Drawing.Icon(asm.GetManifestResourceStream(resName)!)
    : System.Drawing.SystemIcons.Application;

// after
System.Drawing.Icon icon = TrayIconLoader.Load(System.Windows.Forms.SystemInformation.SmallIconSize);
```

Leave the rest of `SetupTrayIcon` unchanged.

- [ ] **Step 5b: Dispose the icon on exit**

`TrayIconLoader.Load` returns a caller-owned `Icon` on **both** paths, so the
caller must dispose it. Today nothing does: disposing a `NotifyIcon` does not
dispose its `Icon`, so the GDI handle leaks. Without this step the port would
also turn the fallback path — which previously handed back the shared
`SystemIcons.Application` — into a leaked clone.

In `UI/MainWindow.xaml.cs`, in the method containing `trayIcon?.Dispose()`
(around line 121):

```csharp
// before
trayIcon?.Dispose();
trayIcon = null;

// after
// NotifyIcon.Dispose() does not dispose the Icon assigned to it, and
// TrayIconLoader.Load hands back an instance we own on both paths.
System.Drawing.Icon? trayIconImage = trayIcon?.Icon;
trayIcon?.Dispose();
trayIconImage?.Dispose();
trayIcon = null;
```

Read the `Icon` *before* disposing the `NotifyIcon` — reading a disposed
component's property is undefined.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test AudioStreamer.Tests`

Expected: PASS. 23 existing + 4 new = 27, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add UI/TrayIconLoader.cs UI/MainWindow.xaml.cs AudioStreamer.Tests/TrayIconLoaderTests.cs
git commit -m "fix: load the 16px icon frame for the tray

new Icon(stream) selects the .ico's default 32x32 frame and lets NotifyIcon
downscale it, blurring the tray icon. Extract loading into TrayIconLoader and
request SystemInformation.SmallIconSize so GDI+ picks the matching frame.

Also dispose the Icon on exit: NotifyIcon.Dispose() does not dispose it, and
Load now returns an instance the caller owns on both paths."
```

---

### Task 2: Generate the new icon

The `.ico` is a binary asset; this task's gate is **visual**, not an assertion. Task 3 adds the regression tests.

**Files:**
- Create: `tools/generate-icon.py`
- Modify (regenerate): `Resources/icon.ico`
- Modify: `.gitignore` (ignore `build/`, used for preview renders)

**Interfaces:**
- Consumes: nothing.
- Produces: `Resources/icon.ico` with six PNG-encoded frames; 16 and 32 drawn from `SIMPLE` geometry.

- [ ] **Step 1: Confirm Pillow**

Run: `python -c "import PIL; print(PIL.__version__)"`

Expected: `8.1.0` or later (`append_images` support for ICO). If missing: `python -m pip install Pillow`.

- [ ] **Step 2: Write the generator**

Create `tools/generate-icon.py`:

```python
#!/usr/bin/env python3
"""Regenerate Resources/icon.ico.

A blue rounded-square tile carrying a white speaker-and-waves glyph. The tile
geometry is shared with AudioLeash's icon (corner radius 15.3% of width,
vertical gradient, white glyph) so the two apps read as a family.

Sibling script: AudioLeash/tools/generate-icon.py. The two share ~120 lines of
machinery and differ only in the APP config block below. A fix to one very
likely belongs in the other.

Small frames (16, 32) are drawn from SIMPLE geometry, not downscaled from a
large one: at 16px the three hairline arcs shatter into specks. Every frame is
supersampled 8x and reduced with LANCZOS; the small frames have their positions
snapped to the device pixel grid first.

Usage:
    python tools/generate-icon.py            # write the .ico
    python tools/generate-icon.py --preview  # also dump PNGs for inspection
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw

SS = 8                       # supersampling factor
SIZES = [16, 32, 48, 64, 128, 256]
SIMPLIFIED_UP_TO = 32        # sizes <= this use the SIMPLE geometry

# ---- APP CONFIG -------------------------------------------------------------
# Measured from the previous icon's 256px frame at the tile's extreme rows.
GRADIENT_TOP = (0x38, 0x8E, 0xFF)
GRADIENT_BOTTOM = (0x14, 0x57, 0xC8)
CORNER_RADIUS_FRAC = 0.153

# All coordinates are fractions of the tile's width.
# Arc geometry for SIMPLE was found by measurement, not taste: thinner arcs
# shatter, thicker arcs merge into each other or the speaker. See the spec.
FULL = {
    "plate": (0.270, 0.415, 0.360, 0.585),
    "cone": ((0.360, 0.415), (0.500, 0.260), (0.500, 0.740), (0.360, 0.585)),
    # The arcs radiate from the cone's mouth (x=0.52), as in the original
    # artwork -- not from behind the speaker, which makes them cut through it.
    # With sweep 65 deg: inner arc's leftmost x = 0.579, clear of the cone's
    # right edge at 0.500; outer arc spans y 0.246..0.754 and reaches x 0.800,
    # matching the original glyph's bounding box (0.258..0.746, right 0.805).
    "arc_center": (0.520, 0.500),
    "arc_radii": (0.140, 0.210, 0.280),
    "arc_stroke": 0.029,
    "arc_sweep": 65.0,
}

SIMPLE = {
    "plate": (0.1250, 0.3750, 0.2500, 0.6250),
    "cone": ((0.2500, 0.3750), (0.4375, 0.2500), (0.4375, 0.7500), (0.2500, 0.6250)),
    "arc_center": (0.2500, 0.5000),
    "arc_radii": (0.5000, 0.7500),
    "arc_stroke": 0.09375,          # 1.5px at 16px
    "arc_sweep": 62.0,
}
# ---- END APP CONFIG ---------------------------------------------------------

WHITE = (255, 255, 255, 255)
CLEAR = (255, 255, 255, 0)


def _tile(n: int) -> Image.Image:
    """The rounded-square gradient tile, n x n pixels."""
    column = Image.new("RGB", (1, n))
    for y in range(n):
        t = y / (n - 1)
        column.putpixel(
            (0, y),
            tuple(round(a + (b - a) * t) for a, b in zip(GRADIENT_TOP, GRADIENT_BOTTOM)),
        )
    base = column.resize((n, n), Image.NEAREST).convert("RGBA")

    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, n - 1, n - 1], radius=CORNER_RADIUS_FRAC * n, fill=255
    )
    base.putalpha(mask)
    return base


def _glyph(n: int, spec: dict, snap: int) -> Image.Image:
    """The white speaker + arcs, n x n pixels, transparent background.

    `snap` > 0 rounds POSITIONS to that many supersampled pixels so edges land on
    whole device pixels after reduction. The arc STROKE is deliberately not
    snapped: at 16px the stroke is 1.5 device pixels, and rounding it up to 2
    makes adjacent arcs merge into one mass.
    """

    def p(fraction: float) -> float:
        value = fraction * n
        return round(value / snap) * snap if snap else value

    layer = Image.new("RGBA", (n, n), CLEAR)
    draw = ImageDraw.Draw(layer)

    draw.rectangle([p(v) for v in spec["plate"]], fill=WHITE)
    draw.polygon([(p(x), p(y)) for x, y in spec["cone"]], fill=WHITE)

    cx, cy = p(spec["arc_center"][0]), p(spec["arc_center"][1])
    stroke = max(int(spec["arc_stroke"] * n), 1)   # NOT snapped — see docstring
    sweep = spec["arc_sweep"]

    for radius_frac in spec["arc_radii"]:
        r = p(radius_frac)
        draw.arc(
            [cx - r, cy - r, cx + r, cy + r],
            start=-sweep,
            end=sweep,
            fill=WHITE,
            width=stroke,
        )

    return layer


def render(size: int) -> Image.Image:
    """One fully-composited frame at `size` x `size`."""
    simplified = size <= SIMPLIFIED_UP_TO
    spec = SIMPLE if simplified else FULL

    n = size * SS
    composed = Image.alpha_composite(_tile(n), _glyph(n, spec, SS if simplified else 0))
    return composed.resize((size, size), Image.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", action="store_true", help="also dump PNGs")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    repo = Path(__file__).resolve().parent.parent
    out = args.out or repo / "Resources" / "icon.ico"
    out.parent.mkdir(parents=True, exist_ok=True)

    frames = {size: render(size) for size in SIZES}

    # Pillow's ICO writer matches append_images by exact size and embeds them
    # verbatim; only sizes with no matching image get thumbnail()-downscaled.
    # Supplying all six means none is ever downscaled.
    frames[256].save(
        out,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=[frames[s] for s in SIZES if s != 256],
    )
    print(f"wrote {out} ({out.stat().st_size} bytes)")

    if args.preview:
        preview_dir = repo / "build" / "icon-preview"
        preview_dir.mkdir(parents=True, exist_ok=True)
        for size, image in frames.items():
            image.save(preview_dir / f"icon_{size}.png")
            image.resize((size * 10, size * 10), Image.NEAREST).save(
                preview_dir / f"icon_{size}_zoom.png"
            )
        print(f"previews in {preview_dir}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: Render previews and check the mass count numerically**

Run: `python tools/generate-icon.py --preview --out build/icon-preview/candidate.ico`

Then measure the 16px frame before looking at anything. Save this as `build/measure.py` (scratch, not committed):

```python
from PIL import Image

def lum(p):
    r, g, b, _ = p
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255

im = Image.open("build/icon-preview/icon_16.png").convert("RGBA")
px = im.load()
seen, sizes = set(), []
for y in range(16):
    for x in range(16):
        if (x, y) in seen or px[x, y][3] < 128 or lum(px[x, y]) <= 0.80:
            continue
        stack, count = [(x, y)], 0
        while stack:
            cx, cy = stack.pop()
            if (cx, cy) in seen:
                continue
            seen.add((cx, cy)); count += 1
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):          # 8-connectivity
                    nx, ny = cx + dx, cy + dy
                    if 0 <= nx < 16 and 0 <= ny < 16 and (nx, ny) not in seen \
                       and px[nx, ny][3] >= 128 and lum(px[nx, ny]) > 0.80:
                        stack.append((nx, ny))
        sizes.append(count)

opaque = [p for p in im.getdata() if p[3] >= 128]
near_white = sum(1 for p in opaque if lum(p) > 0.95)
print(f"masses={len(sizes)} sizes={sorted(sizes, reverse=True)} near_white={near_white}")
```

Run: `python build/measure.py`

Expected, from the spec's prototype: `masses=3 sizes=[25, 23, 21] near_white=47`

Small deviations are fine. **These are not:** any mass smaller than 8 pixels (an arc has crumbled), fewer than 3 masses (arcs merged), more than 3 (fragmentation).

- [ ] **Step 4: Look at the frames**

Open `build/icon-preview/icon_16_zoom.png` and `icon_256.png`. Check:

1. At 16px: speaker plus two clean, separate arcs. No specks.
2. At 256px: three arcs, evenly spaced, not touching the speaker cone.
3. Put the new 256px next to the original (`git stash` is not needed — extract the old frame from git: `git show HEAD:Resources/icon.ico > build/old.ico`, then open its 256px frame). The redrawn arcs must not look worse than the originals.
4. Corners match AudioLeash's tile.

- [ ] **Step 5: Tune until Steps 3 and 4 both pass**

Both config blocks were rendered and checked before this plan was written: `SIMPLE`
reproduces `masses=3 sizes=[25, 23, 21] near_white=47`, and `FULL` produces three
clean arcs clear of the cone. You may well not need to tune at all. If you do,
adjust only the `FULL` / `SIMPLE` dicts, then re-run Steps 3 and 4.

Levers, most likely first:

- **256px arcs cut through the cone** → the arc centre must sit at the cone's mouth (`0.52`), not behind the speaker. Moving it left is what causes this.
- **256px arcs look thin or spindly** → raise `FULL["arc_stroke"]` (0.029 ≈ 7.4px at 256).
- **256px arcs unevenly spaced** → the radii must stay an arithmetic progression.
- **16px masses ≠ 3** → do NOT touch `SIMPLE["arc_stroke"]` first. 0.09375 is 1.5px at 16px and was measured to be the only value that works: at 1px the arcs shatter, at 2px they merge. Adjust `SIMPLE["arc_radii"]` spacing instead.
- **16px arcs merge into the speaker** → pull the cone's right edge (`0.4375`) left, or push `SIMPLE["arc_radii"]` outward.

Full regeneration was chosen deliberately: if the redrawn 256px frame is worse, the answer is to keep tuning, **not** to preserve the old large frames.

- [ ] **Step 6: Write the real icon and verify its frames**

```bash
python tools/generate-icon.py
python -c "from PIL import Image; print(sorted(Image.open('Resources/icon.ico').ico.sizes()))"
```

Expected: `[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]`

- [ ] **Step 7: Confirm determinism and that the app builds**

```bash
sha256sum Resources/icon.ico
python tools/generate-icon.py
sha256sum Resources/icon.ico     # must be identical
dotnet build -c Release
```

Expected: identical hashes; clean build.

- [ ] **Step 8: Launch the app and look at the real tray**

Run the built app, minimise it to the tray, and inspect the notification area. The icon must show a discernible speaker and two arcs. Compare against AudioLeash's icon if it is running.

Unlike AudioLeash, AudioStreamer has no single-instance mutex, so it launches directly.

- [ ] **Step 9: Commit**

Add `build/` to `.gitignore` in this commit (verify with `git status --short` that no preview PNGs are staged).

```bash
git add .gitignore tools/generate-icon.py Resources/icon.ico
git commit -m "feat: redesign the icon for legibility at tray size

The three hairline wave arcs shattered into four disconnected specks at 16px.
The 16px and 32px frames are now drawn from simplified geometry -- speaker plus
two thickened arcs, positions snapped to whole device pixels -- rather than
downscaled from a large master.

Arc geometry was found by measurement: thinner arcs fragment, thicker arcs merge.
tools/generate-icon.py is the source of truth and reproduces the .ico
deterministically."
```

---

### Task 3: Lock the icon in with regression tests

Must land **after** Task 2: these assertions fail against the old icon.

**Files:**
- Create: `AudioStreamer.Tests/IconAssetTests.cs`

**Interfaces:**
- Consumes: `AudioStreamer.TrayIconLoader` (Task 1) — only to reach the app assembly. `Resources/icon.ico` (Task 2).
- Produces: nothing consumed downstream.

- [ ] **Step 1: Write the tests**

Create `AudioStreamer.Tests/IconAssetTests.cs`:

```csharp
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xunit;

namespace AudioStreamer.Tests
{
    public class IconAssetTests
    {
        // The prototype scores 47 near-white pixels; the icon this replaced scored 11.
        private const int MinimumNearWhitePixels = 30;

        // A crumbled arc leaves a fleck. The prototype's components are 25/23/21.
        private const int MinimumComponentPixels = 8;

        private sealed record Frame(int Width, int Height, byte[] Payload);

        private static byte[] ReadIcon()
        {
            var asm = typeof(TrayIconLoader).Assembly;
            string resName = System.Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase))!;

            using var stream = asm.GetManifestResourceStream(resName)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>
        /// Parses the ICO directory. Frame payloads are decoded here rather than
        /// via Icon.ToBitmap(), which is unreliable for PNG-compressed frames.
        /// </summary>
        private static List<Frame> ReadFrames()
        {
            byte[] ico = ReadIcon();
            int count = System.BitConverter.ToUInt16(ico, 4);
            var frames = new List<Frame>(count);

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int width = ico[entry] == 0 ? 256 : ico[entry];
                int height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
                int length = System.BitConverter.ToInt32(ico, entry + 8);
                int offset = System.BitConverter.ToInt32(ico, entry + 12);

                if (offset < 0 || length < 0 || (long)offset + length > ico.Length)
                    throw new System.InvalidOperationException(
                        $"icon.ico frame {i} is out of bounds: offset={offset}, length={length}, file={ico.Length}");

                frames.Add(new Frame(width, height, ico.AsSpan(offset, length).ToArray()));
            }

            return frames;
        }

        private static Bitmap Decode(Frame frame)
        {
            Assert.True(
                frame.Payload.Length > 8 && frame.Payload[1] == 'P' &&
                frame.Payload[2] == 'N' && frame.Payload[3] == 'G',
                $"expected a PNG-encoded {frame.Width}px frame; regenerate with tools/generate-icon.py");

            using var stream = new MemoryStream(frame.Payload);
            using var decoded = new Bitmap(stream);

            // A Bitmap built from a stream needs that stream to outlive it, so
            // hand back a copy that owns its own pixel data.
            return new Bitmap(decoded);
        }

        private static double Luminance(Color c) =>
            (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        /// <summary>
        /// Sizes of the 8-connected components of bright (glyph) pixels, largest first.
        /// </summary>
        /// <remarks>
        /// 8-connectivity, not 4: a 1px-wide curved stroke steps diagonally, and
        /// under 4-connectivity each diagonal step severs it. The two arcs would
        /// report as a handful of specks even when the artwork is correct.
        /// </remarks>
        private static List<int> BrightComponentSizes(Bitmap frame)
        {
            bool IsBright(int x, int y)
            {
                Color c = frame.GetPixel(x, y);
                return c.A >= 128 && Luminance(c) > 0.80;
            }

            var seen = new bool[16, 16];
            var sizes = new List<int>();

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                if (seen[x, y] || !IsBright(x, y)) continue;

                int count = 0;
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    if (cx < 0 || cx > 15 || cy < 0 || cy > 15) continue;
                    if (seen[cx, cy] || !IsBright(cx, cy)) continue;

                    seen[cx, cy] = true;
                    count++;

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            stack.Push((cx + dx, cy + dy));
                }

                sizes.Add(count);
            }

            sizes.Sort();
            sizes.Reverse();
            return sizes;
        }

        [Fact]
        public void Icon_ContainsAllSixSizes()
        {
            int[] sizes = ReadFrames().Select(f => f.Width).OrderBy(w => w).ToArray();

            Assert.Equal(new[] { 16, 32, 48, 64, 128, 256 }, sizes);
        }

        [Fact]
        public void SmallFrame_HasCrispWhitePixels()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            int nearWhite = 0;
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color c = frame.GetPixel(x, y);
                if (c.A >= 128 && Luminance(c) > 0.95) nearWhite++;
            }

            Assert.True(
                nearWhite >= MinimumNearWhitePixels,
                $"only {nearWhite} near-white pixels at 16px (need >= {MinimumNearWhitePixels}); " +
                "the glyph has dissolved into antialiased grey — is it being downscaled?");
        }

        /// <summary>
        /// The speaker and the two arcs must survive as three separate shapes.
        /// Merging means the icon reads as a blob; fragmenting means it has
        /// dissolved into the specks that made the old icon illegible.
        /// </summary>
        [Fact]
        public void SmallFrame_ResolvesIntoExactlyThreeMasses()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            Assert.Equal(3, BrightComponentSizes(frame).Count);
        }

        /// <summary>
        /// An exact mass count is not enough on its own: three masses of which one
        /// is a three-pixel fleck would still count as three.
        /// </summary>
        [Fact]
        public void SmallFrame_HasNoCrumbledComponent()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            List<int> sizes = BrightComponentSizes(frame);

            // Not sizes.Min() inline: xUnit builds the message eagerly, so an
            // empty list would throw InvalidOperationException instead of failing.
            int smallest = sizes.Count == 0 ? 0 : sizes[^1];

            Assert.True(
                sizes.Count > 0 && smallest >= MinimumComponentPixels,
                $"smallest bright component is {smallest} pixels (need >= {MinimumComponentPixels}); " +
                $"an arc has crumbled. Component sizes: [{string.Join(", ", sizes)}]");
        }
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test AudioStreamer.Tests --filter FullyQualifiedName~IconAssetTests`

Expected: 4 passing.

If `SmallFrame_ResolvesIntoExactlyThreeMasses` or `SmallFrame_HasNoCrumbledComponent` fails, the **artwork** regressed — go back to Task 2 Step 5. **Do not** lower `MinimumNearWhitePixels`, `MinimumComponentPixels`, or the expected mass count to make a red test green.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test AudioStreamer.Tests`

Expected: 27 + 4 = 31 passing, 0 failed.

- [ ] **Step 4: Prove the guards actually catch the defect**

A regression test nobody has watched fail is not yet a guard.

Temporarily set `SIMPLIFIED_UP_TO = 0` in `tools/generate-icon.py` (so every frame uses the hairline `FULL` geometry, reproducing the old defect), regenerate, and re-run.

```bash
python tools/generate-icon.py
dotnet test AudioStreamer.Tests --filter FullyQualifiedName~IconAssetTests
```

Expected: `SmallFrame_ResolvesIntoExactlyThreeMasses` **FAILS** (more than 3 masses), and `SmallFrame_HasNoCrumbledComponent` **FAILS** (a component of ~2-6 pixels). Record the verbatim output and the observed numbers. `SmallFrame_HasCrispWhitePixels` may also fail.

If either mass test *passes* on the broken artwork, stop and report BLOCKED — the guard does not work and papering over it defeats the task.

Then revert `SIMPLIFIED_UP_TO` to `32`, regenerate, and confirm:

```bash
python tools/generate-icon.py
git status --short          # Resources/icon.ico must be unmodified
dotnet test AudioStreamer.Tests
```

Do **not** commit the mutated icon.

- [ ] **Step 5: Commit**

```bash
git add AudioStreamer.Tests/IconAssetTests.cs
git commit -m "test: guard icon legibility at tray size

Assert the .ico carries all six frames, that the 16px frame keeps at least 30
near-white pixels, that it resolves into exactly three 8-connected bright
masses, and that no component has crumbled below 8 pixels.

8-connectivity, not 4: a 1px curved stroke steps diagonally, so 4-connectivity
would report correct arcs as a scatter of specks."
```

---

### Task 4: Documentation

**Files:**
- Modify: `CLAUDE.md`
- Modify: `README.md` (only if it references the icon — check first)

**Interfaces:**
- Consumes: `tools/generate-icon.py` (Task 2), `UI/TrayIconLoader.cs` (Task 1).
- Produces: nothing.

- [ ] **Step 1: Check whether README describes the icon**

Run: `grep -rin "icon" README.md`

An icon's *appearance* changing is not user-facing behaviour. If README only mentions tray behaviour (minimise to tray, right-click menu), leave it alone and say so in your report. Do not invent a README section.

- [ ] **Step 2: Add a "Regenerating the icon" block to `CLAUDE.md`**

`CLAUDE.md` has no project-structure tree — it is prose plus fenced command blocks. Match that style. Add after the Installer block:

````markdown
### Regenerating the icon

```powershell
python -m pip install Pillow            # dev-time only; not a build dependency
python tools/generate-icon.py           # rewrites Resources/icon.ico
python tools/generate-icon.py --preview # also dumps PNGs to build/icon-preview/
```

`Resources/icon.ico` is generated, not hand-drawn; `tools/generate-icon.py` is the source of truth and reproduces it byte-for-byte with the same Pillow version. It renders six frames (16, 32, 48, 64, 128, 256). The 16px and 32px frames are **redrawn from simplified geometry** — speaker plus two thickened arcs, positions snapped to whole device pixels — not downscaled from a large master; the three hairline arcs of the full glyph shatter into specks at tray size. `IconAssetTests` enforces this: the 16px frame must resolve into exactly three 8-connected bright masses with no component under 8 pixels. Do not relax those bounds to make a red test green — they mean the artwork regressed.

The blue tile (corner radius 15.3% of width, gradient `#388EFF` → `#1457C8`) is shared with AudioLeash's icon so the two apps read as a family. The generator is a near-copy of `AudioLeash/tools/generate-icon.py`; a fix to one likely belongs in the other.
````

- [ ] **Step 3: Verify only the intended files changed**

Run: `git status --short`

Expected: only `CLAUDE.md` (and `README.md` if you justified changing it).

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document icon regeneration"
```

---

## Done When

- [ ] `dotnet test AudioStreamer.Tests` passes — 31 tests, 0 failed.
- [ ] The tray icon is recognisable at 16px on a real Windows desktop, verified by looking at it.
- [ ] `python tools/generate-icon.py` leaves `git status` clean.
- [ ] Task 3 Step 4 was performed: both mass guards were **observed failing** on deliberately broken artwork.
- [ ] No changes to `AudioStreamer.csproj` or `installer/AudioStreamer.iss`.
- [ ] The new 256px frame was compared side by side against the original and does not look worse.
