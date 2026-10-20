"""Draws pilot's app icon: a paper plane on the dashboard's navy, a green trail behind it. Writes PNGs and a multi-size ICO."""
import struct
import sys
import zlib
from pathlib import Path

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

BG_TOP = (0x23, 0x34, 0x58)
BG_BOTTOM = (0x14, 0x1c, 0x30)
WING = (0xe6, 0xee, 0xfc)
WING_LOW = (0x8a, 0xb4, 0xf8)
FOLD = (0x4e, 0x72, 0xb8)
TRAIL = (0x4c, 0xc3, 0x8a)

# Shapes in unit coordinates (y down), painted in order.
TOP_WING = [(0.13, 0.50), (0.87, 0.15), (0.43, 0.61)]
LOW_WING = [(0.43, 0.61), (0.87, 0.15), (0.57, 0.85)]
FOLD_TRI = [(0.43, 0.61), (0.57, 0.85), (0.47, 0.82)]
TRAIL_DOTS = [(0.17, 0.83, 0.045), (0.27, 0.75, 0.040), (0.36, 0.69, 0.034)]
RADIUS = 0.22


def inside_tri(px, py, tri):
    (x1, y1), (x2, y2), (x3, y3) = tri
    d1 = (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2)
    d2 = (px - x3) * (y2 - y3) - (x2 - x3) * (py - y3)
    d3 = (px - x1) * (y3 - y1) - (x3 - x1) * (py - y1)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def inside_rounded(px, py):
    cx = min(max(px, RADIUS), 1 - RADIUS)
    cy = min(max(py, RADIUS), 1 - RADIUS)
    return (px - cx) ** 2 + (py - cy) ** 2 <= RADIUS ** 2


def colour_at(px, py):
    """The colour at a unit point, or None outside the rounded square."""
    if not inside_rounded(px, py):
        return None
    t = py
    c = tuple(round(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t) for i in range(3))
    for (x, y, r) in TRAIL_DOTS:
        if (px - x) ** 2 + (py - y) ** 2 <= r * r:
            c = TRAIL
    if inside_tri(px, py, LOW_WING):
        c = WING_LOW
    if inside_tri(px, py, TOP_WING):
        c = WING
    if inside_tri(px, py, FOLD_TRI):
        c = FOLD
    return c


def render(size, ss=4):
    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            r = g = b = a = 0
            for sy in range(ss):
                for sx in range(ss):
                    c = colour_at((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size)
                    if c is not None:
                        r += c[0]; g += c[1]; b += c[2]; a += 1
            n = ss * ss
            if a:
                row += bytes((r // a, g // a, b // a, round(255 * a / n)))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


sizes = [16, 24, 32, 48, 64, 128, 256]
images = {}
for s in sizes:
    images[s] = png(s, render(s, ss=8 if s <= 32 else 4))
    (out / f"pilot-{s}.png").write_bytes(images[s])

# ICO: PNG-compressed entries, which Windows reads from Vista on, at every size.
header = struct.pack("<HHH", 0, 1, len(sizes))
entries = b""
data = b""
offset = 6 + 16 * len(sizes)
for s in sizes:
    blob = images[s]
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset)
    data += blob
    offset += len(blob)
(out / "pilot.ico").write_bytes(header + entries + data)
print("wrote", ", ".join(f"pilot-{s}.png" for s in sizes), "and pilot.ico")
