"""Draw the accelerator comparison's map side by side (silhouette plan P0.3, D8).

Reads a map file written by /vhsilhouetteserver compare: gzip CSV with x, z, the real height, its
origin, and for each accelerator the height sampled now and, where a presample existed, the height
sampled before the land was loaded. Writes three PNG panels and an HTML page beside the map:

- the real terrain;
- the accelerator's terrain (sampled before the land existed where the map has it, otherwise
  sampled now), with the real coastline drawn over it;
- where they differ: blue reads low, red reads high, magenta where land and sea disagree.

Standard library only. Usage:
  python scripts/silhouette-compare-render.py <map.csv.gz> [accelerator index, default 0]
"""
import base64
import gzip
import html
import struct
import sys
import zlib
from pathlib import Path

CELL = 4
MISSING = (60, 60, 60)
LAND_STOPS = [(0, (205, 195, 135)), (4, (115, 160, 85)), (40, (70, 120, 60)),
              (90, (135, 115, 85)), (140, (170, 170, 170)), (180, (245, 245, 245))]


def read_map(path):
    meta, rows = {"acc": [], "sea": 110}, []
    with gzip.open(path, "rt", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if line.startswith("# accelerators "):
                meta["acc"] = line[len("# accelerators "):].split("|")
            elif line.startswith("# sealevel "):
                meta["sea"] = int(line[len("# sealevel "):])
            elif line and not line.startswith("#") and not line.startswith("x,"):
                rows.append(line.split(","))
    return meta, rows


def png(width, height, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[y * width * 3:(y + 1) * width * 3]) for y in range(height))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def terrain(h, sea, shade):
    if h < sea:
        return lerp((140, 185, 225), (25, 60, 130), min(1.0, (sea - h) / 40.0))
    above = h - sea
    for (h0, c0), (h1, c1) in zip(LAND_STOPS, LAND_STOPS[1:]):
        if above <= h1:
            c = lerp(c0, c1, (above - h0) / (h1 - h0))
            break
    else:
        c = LAND_STOPS[-1][1]
    return tuple(max(0, min(255, int(v * shade))) for v in c)


def difference(real, sampled, sea):
    if (real >= sea) != (sampled >= sea):
        return (220, 0, 220)
    t = min(1.0, abs(sampled - real) / 8.0)
    return lerp((255, 255, 255), (200, 30, 30) if sampled > real else (30, 70, 200), t)


def main():
    path = Path(sys.argv[1])
    index = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    meta, rows = read_map(path)
    sea, name = meta["sea"], meta["acc"][index] if index < len(meta["acc"]) else "accelerator"
    now_col, before_col = 4 + 2 * index, 5 + 2 * index
    use_before = any(r[before_col] for r in rows)
    col = before_col if use_before else now_col
    xs, zs = [int(r[0]) for r in rows], [int(r[1]) for r in rows]
    x0, z0 = min(xs), min(zs)
    w, h = (max(xs) - x0) // CELL + 1, (max(zs) - z0) // CELL + 1
    real = [[None] * w for _ in range(h)]
    samp = [[None] * w for _ in range(h)]
    for r in rows:
        i, j = (int(r[0]) - x0) // CELL, (int(r[1]) - z0) // CELL
        real[j][i] = int(r[2])
        samp[j][i] = int(r[col]) if r[col] else None

    def shade(grid, i, j):
        def at(a, b):
            v = grid[max(0, min(h - 1, b))][max(0, min(w - 1, a))]
            return grid[j][i] if v is None else v
        return max(0.6, min(1.3, 1 + ((at(i - 1, j) - at(i + 1, j)) + (at(i, j - 1) - at(i, j + 1))) * 0.04))

    panels = {k: bytearray(w * h * 3) for k in ("real", "sampled", "diff")}
    errors, flips, signed = [], 0, 0
    for j in range(h):
        for i in range(w):
            o = (j * w + i) * 3
            rv, sv = real[j][i], samp[j][i]
            pr = terrain(rv, sea, shade(real, i, j)) if rv is not None else MISSING
            ps = terrain(sv, sea, shade(samp, i, j)) if sv is not None else MISSING
            pd = MISSING
            if rv is not None and sv is not None:
                pd = difference(rv, sv, sea)
                errors.append(abs(sv - rv))
                signed += sv - rv
                flips += (rv >= sea) != (sv >= sea)
            if rv is not None and any(0 <= a < w and 0 <= b < h and real[b][a] is not None
                                      and (real[b][a] >= sea) != (rv >= sea)
                                      for a, b in ((i + 1, j), (i, j + 1))):
                ps = (15, 15, 15)
            panels["real"][o:o + 3], panels["sampled"][o:o + 3], panels["diff"][o:o + 3] = bytes(pr), bytes(ps), bytes(pd)

    stem = path.name.replace(".csv.gz", "")
    images = {}
    for key, pixels in panels.items():
        data = png(w, h, pixels)
        (path.parent / f"{stem}-{key}.png").write_bytes(data)
        images[key] = base64.b64encode(data).decode("ascii")
    errors.sort()
    n = len(errors)
    summary = (f"{n} cells; typical error {errors[n // 2] if n else 0} blocks, 95% within "
               f"{errors[min(n - 1, int(n * 0.95))] if n else 0}; average lean {signed / n if n else 0:+.1f} blocks; "
               f"land and sea disagree at {flips} cells ({100.0 * flips / n if n else 0:.1f}%)")
    when = "sampled before the land existed" if use_before else "sampled after the land was generated"
    caption = {"real": "Real terrain, as the world generator made it",
               "sampled": f"{html.escape(name)}, {when}; black line: the real coastline",
               "diff": "Difference: red reads high, blue reads low (full colour at 8 blocks); magenta: land and sea disagree"}
    figures = "".join(f'<figure><img src="data:image/png;base64,{images[k]}" alt="{k}"><figcaption>{caption[k]}'
                      f'</figcaption></figure>' for k in ("real", "sampled", "diff"))
    page = (f"<!doctype html><meta charset='utf-8'><title>Silhouette accelerator side-by-side</title>"
            f"<style>body{{font-family:sans-serif;margin:16px;background:#f4f4f2;color:#222}}"
            f"div{{display:flex;flex-wrap:wrap;gap:12px}}figure{{margin:0;max-width:100%}}"
            f"img{{width:{max(w, 260)}px;max-width:100%;image-rendering:pixelated;border:1px solid #999}}"
            f"figcaption{{max-width:{max(w, 260)}px;font-size:13px}}</style>"
            f"<h1>Silhouette accelerator side-by-side</h1><p>{html.escape(summary)}. North is up; one pixel is "
            f"{CELL} blocks; grey means no data. Sea level {sea}.</p><div>{figures}</div>")
    out = path.parent / f"{stem}.html"
    out.write_text(page, encoding="utf-8")
    print(summary)
    print(out)


if __name__ == "__main__":
    main()
