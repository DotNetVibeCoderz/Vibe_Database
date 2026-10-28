"""Render the FAISS.Net package icons.

Writes icon.svg / icon.png (Gravicode.FaissNet) and icon-gpu.svg / icon-gpu.png
(Gravicode.FaissNet.Gpu) at 512px, plus 128px and 64px previews, next to this script. The SVG and
the PNG come from the same geometry constants below, so the vector source and the raster cannot
drift apart; edit the constants, never the SVG by hand.

The design is a k-nearest-neighbour search drawn literally: an amber query vector, links to its
nearest cyan neighbours, the search radius around them, and the rest of the database left grey
outside it. Everything is a dot, a line or a ring, so it survives the drop to 32px in the NuGet
search results. The GPU variant adds a chip outline around the same search.

Only Pillow is required. Drawing happens at 4x and is downsampled with LANCZOS for anti-aliasing.

    python -I assets/render_icon.py
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

OUT = Path(__file__).resolve().parent

# ---------------------------------------------------------------------------------------------
# Palette -- from samples/Faiss.Net.Gallery/Theme. Amber marks "the query" in the Gallery too,
# and cyan marks "a result", so the icon reads with the same vocabulary as the app.
# ---------------------------------------------------------------------------------------------
TILE_TOP = "#1E2436"      # Raised
TILE_BOTTOM = "#10131C"   # Ink
TILE_RULE = "#262D42"     # Rule
AMBER = "#F0A22E"
AMBER_LIGHT = "#FFD27A"
CYAN = "#3EC8D8"
CYAN_DIM = "#1F6E78"      # between CyanDim and Cyan: the search ring must stay visible at 32px
MUTE = "#4A5168"          # far points: present, but clearly not part of the answer

# ---------------------------------------------------------------------------------------------
# Geometry, in a 512 x 512 design space.
# ---------------------------------------------------------------------------------------------
SIZE = 512
TILE_RADIUS = 112
RULE_WIDTH = 4

QUERY = (256, 256)
QUERY_R = 40
HALO_R = 74               # soft amber glow behind the query
RING_R = 162              # the search radius
RING_WIDTH = 10

# Nearest neighbours: (angle in degrees, distance from the query, dot radius). Distances vary so
# the picture says "ranked by distance", not "evenly spaced".
NEIGHBOURS = [
    (-62, 118, 25),
    (8, 104, 27),
    (74, 128, 23),
    (152, 96, 25),
    (-138, 134, 21),
]
LINK_WIDTH = 11

# Database points outside the radius.
FAR = [
    (430, 92, 15), (452, 214, 12), (440, 420, 15), (318, 462, 12),
    (84, 448, 13), (66, 116, 12), (180, 64, 11), (468, 318, 10),
]

# GPU variant: a chip outline with pins, inset inside the tile.
CHIP_INSET = 52
CHIP_RADIUS = 56
CHIP_WIDTH = 9
PIN_LENGTH = 22
PIN_COUNT = 4

SCALE = 4


def neighbour_points() -> list[tuple[float, float, float]]:
    qx, qy = QUERY
    return [
        (qx + dist * math.cos(math.radians(a)), qy + dist * math.sin(math.radians(a)), r)
        for a, dist, r in NEIGHBOURS
    ]


def chip_pins() -> list[tuple[float, float, float, float]]:
    """Pin segments (x1, y1, x2, y2) on all four edges of the chip outline."""
    lo, hi = CHIP_INSET, SIZE - CHIP_INSET
    span = hi - lo - 2 * CHIP_RADIUS
    pins = []
    for i in range(PIN_COUNT):
        t = lo + CHIP_RADIUS + span * (i + 0.5) / PIN_COUNT
        pins += [
            (t, lo - PIN_LENGTH, t, lo), (t, hi, t, hi + PIN_LENGTH),
            (lo - PIN_LENGTH, t, lo, t), (hi, t, hi + PIN_LENGTH, t),
        ]
    return pins


# ---------------------------------------------------------------------------------------------
# SVG
# ---------------------------------------------------------------------------------------------
def svg(gpu: bool) -> str:
    qx, qy = QUERY
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" viewBox="0 0 {SIZE} {SIZE}">',
        "<defs>",
        f'<linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TILE_TOP}"/>'
        f'<stop offset="1" stop-color="{TILE_BOTTOM}"/></linearGradient>',
        f'<radialGradient id="halo"><stop offset="0" stop-color="{AMBER}" stop-opacity="0.55"/>'
        f'<stop offset="1" stop-color="{AMBER}" stop-opacity="0"/></radialGradient>',
        f'<radialGradient id="query" cx="0.38" cy="0.34" r="0.75"><stop offset="0" stop-color="{AMBER_LIGHT}"/>'
        f'<stop offset="1" stop-color="{AMBER}"/></radialGradient>',
        "</defs>",
        f'<rect x="{RULE_WIDTH / 2}" y="{RULE_WIDTH / 2}" width="{SIZE - RULE_WIDTH}" height="{SIZE - RULE_WIDTH}" '
        f'rx="{TILE_RADIUS}" fill="url(#tile)" stroke="{TILE_RULE}" stroke-width="{RULE_WIDTH}"/>',
    ]
    if gpu:
        lo, side = CHIP_INSET, SIZE - 2 * CHIP_INSET
        parts.append(f'<rect x="{lo}" y="{lo}" width="{side}" height="{side}" rx="{CHIP_RADIUS}" fill="none" '
                     f'stroke="{MUTE}" stroke-width="{CHIP_WIDTH}"/>')
        for x1, y1, x2, y2 in chip_pins():
            parts.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{MUTE}" '
                         f'stroke-width="{CHIP_WIDTH}" stroke-linecap="round"/>')
    else:
        for x, y, r in FAR:
            parts.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{MUTE}"/>')
    parts.append(f'<circle cx="{qx}" cy="{qy}" r="{RING_R}" fill="none" stroke="{CYAN_DIM}" '
                 f'stroke-width="{RING_WIDTH}"/>')
    parts.append(f'<circle cx="{qx}" cy="{qy}" r="{HALO_R}" fill="url(#halo)"/>')
    for x, y, _ in neighbour_points():
        parts.append(f'<line x1="{qx}" y1="{qy}" x2="{x:.1f}" y2="{y:.1f}" stroke="{CYAN}" '
                     f'stroke-width="{LINK_WIDTH}" stroke-linecap="round" stroke-opacity="0.75"/>')
    for x, y, r in neighbour_points():
        parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{CYAN}"/>')
    parts.append(f'<circle cx="{qx}" cy="{qy}" r="{QUERY_R}" fill="url(#query)"/>')
    parts.append("</svg>")
    return "\n".join(parts) + "\n"


# ---------------------------------------------------------------------------------------------
# Raster
# ---------------------------------------------------------------------------------------------
def rgb(hex_colour: str) -> tuple[int, int, int]:
    h = hex_colour.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))  # type: ignore[return-value]


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))  # type: ignore[return-value]


def disc(draw: ImageDraw.ImageDraw, x: float, y: float, r: float, fill) -> None:
    s = SCALE
    draw.ellipse([(x - r) * s, (y - r) * s, (x + r) * s, (y + r) * s], fill=fill)


def line(draw: ImageDraw.ImageDraw, x1, y1, x2, y2, width, fill) -> None:
    s = SCALE
    draw.line([(x1 * s, y1 * s), (x2 * s, y2 * s)], fill=fill, width=round(width * s))
    disc(draw, x1, y1, width / 2, fill)  # round caps
    disc(draw, x2, y2, width / 2, fill)


def render(gpu: bool) -> Image.Image:
    s, big = SCALE, SIZE * SCALE
    qx, qy = QUERY

    # Tile: vertical gradient, clipped to the rounded rectangle.
    gradient = Image.new("RGB", (1, big))
    top, bottom = rgb(TILE_TOP), rgb(TILE_BOTTOM)
    for y in range(big):
        gradient.putpixel((0, y), lerp(top, bottom, y / (big - 1)))
    gradient = gradient.resize((big, big))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=TILE_RADIUS * s, fill=255)
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    image.paste(gradient, (0, 0), mask)

    draw = ImageDraw.Draw(image)
    inset = RULE_WIDTH * s / 2
    draw.rounded_rectangle([inset, inset, big - 1 - inset, big - 1 - inset], radius=TILE_RADIUS * s - inset,
                           outline=rgb(TILE_RULE), width=RULE_WIDTH * s)

    if gpu:
        lo, hi = CHIP_INSET * s, (SIZE - CHIP_INSET) * s
        draw.rounded_rectangle([lo, lo, hi, hi], radius=CHIP_RADIUS * s, outline=rgb(MUTE), width=CHIP_WIDTH * s)
        for x1, y1, x2, y2 in chip_pins():
            line(draw, x1, y1, x2, y2, CHIP_WIDTH, rgb(MUTE))
    else:
        for x, y, r in FAR:
            disc(draw, x, y, r, rgb(MUTE))

    draw.ellipse([(qx - RING_R) * s, (qy - RING_R) * s, (qx + RING_R) * s, (qy + RING_R) * s],
                 outline=rgb(CYAN_DIM), width=RING_WIDTH * s)

    # Halo: a blurred amber disc composited under the links.
    halo = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    disc(ImageDraw.Draw(halo), qx, qy, HALO_R * 0.7, (*rgb(AMBER), 150))
    halo = halo.filter(ImageFilter.GaussianBlur(HALO_R * s * 0.35))
    image = Image.alpha_composite(image, halo)

    links = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    link_draw = ImageDraw.Draw(links)
    for x, y, _ in neighbour_points():
        line(link_draw, qx, qy, x, y, LINK_WIDTH, (*rgb(CYAN), 191))
    image = Image.alpha_composite(image, links)

    draw = ImageDraw.Draw(image)
    for x, y, r in neighbour_points():
        disc(draw, x, y, r, rgb(CYAN))

    # Query: radial highlight, offset up-left like the SVG's gradient.
    query = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    qd = ImageDraw.Draw(query)
    light, base = rgb(AMBER_LIGHT), rgb(AMBER)
    hx, hy = qx - QUERY_R * 0.24, qy - QUERY_R * 0.32
    steps = 48
    for i in range(steps):
        t = i / (steps - 1)
        r = QUERY_R * 1.5 * (1 - t)
        disc(qd, hx + (qx - hx) * (1 - t), hy + (qy - hy) * (1 - t), r, lerp(base, light, t))
    qmask = Image.new("L", (big, big), 0)
    disc(ImageDraw.Draw(qmask), qx, qy, QUERY_R, 255)
    image.paste(query, (0, 0), qmask)

    return image.resize((SIZE, SIZE), Image.LANCZOS)


def main() -> None:
    for gpu, stem in ((False, "icon"), (True, "icon-gpu")):
        (OUT / f"{stem}.svg").write_text(svg(gpu), encoding="utf-8")
        image = render(gpu)
        image.save(OUT / f"{stem}.png", optimize=True)
        for size in (128, 64):
            image.resize((size, size), Image.LANCZOS).save(OUT / f"{stem}-{size}.png", optimize=True)
        print(f"wrote {stem}.svg, {stem}.png, {stem}-128.png, {stem}-64.png")


if __name__ == "__main__":
    main()
