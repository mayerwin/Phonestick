"""Builds every raster asset from branding/logo.svg and branding/logo-small.svg.

py -3 branding/tools/build_icons.py
Outputs: branding/png/*.png, windows/Assets/*.ico
States: brand (connected), idle (muted slate), mounted (brand + green badge).
"""
import os, re, sys, tempfile
sys.path.insert(0, os.path.dirname(__file__))
from render import render
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
B = os.path.join(ROOT, "branding")
PNG = os.path.join(B, "png"); os.makedirs(PNG, exist_ok=True)
ICO = os.path.join(ROOT, "windows", "Assets"); os.makedirs(ICO, exist_ok=True)

BRAND = ("#7C3AED", "#5B21B6")
IDLE = ("#64748B", "#334155")

def variant(src, state):
    s = open(os.path.join(B, src), encoding="utf-8").read()
    if state == "idle":
        for a, b in zip(BRAND, IDLE):
            s = s.replace(a, b)
        pass
    if state == "mounted":
        vb = [float(x) for x in re.search(r'viewBox="([^"]+)"', s).group(1).split()]
        w = vb[2]; r = w * 0.2
        badge = (f'<circle cx="{w - r}" cy="{w - r}" r="{r + w * 0.045}" fill="#FFFFFF"/>'
                 f'<circle cx="{w - r}" cy="{w - r}" r="{r}" fill="#22C55E"/>')
        s = s.replace("</svg>", badge + "</svg>")
    return s

def raster(svg_text, size):
    with tempfile.TemporaryDirectory() as td:
        p = os.path.join(td, "v.svg"); open(p, "w", encoding="utf-8").write(svg_text)
        o = os.path.join(td, "v.png"); render(p, o, size)
        return Image.open(o).copy()

def ico(state, out, sizes):
    frames = []
    for sz in sizes:
        src = "logo-small.svg" if sz <= 32 else "logo.svg"
        frames.append(raster(variant(src, state), sz))
    big = frames[-1]
    big.save(out, format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[:-1])

if __name__ == "__main__":
    for sz in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
        raster(variant("logo-small.svg" if sz <= 32 else "logo.svg", "brand"), sz).save(os.path.join(PNG, f"logo-{sz}.png"))
    ico("brand", os.path.join(ICO, "phonestick.ico"), [16, 20, 24, 32, 48, 64, 256])
    for st in ("idle", "brand", "mounted"):
        name = {"brand": "tray-on", "idle": "tray-idle", "mounted": "tray-mounted"}[st]
        ico(st, os.path.join(ICO, name + ".ico"), [16, 20, 24, 32, 40, 48, 64])
        raster(variant("logo-small.svg", st), 64).save(os.path.join(PNG, name + "-64.png"))
    print("ok")
