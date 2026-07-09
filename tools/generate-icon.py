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
