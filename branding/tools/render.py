"""Render SVG files to PNG with headless Chrome, then downscale with Pillow.

py -3 branding/tools/render.py in.svg out.png SIZE [SUPERSAMPLE]
Renders at SIZE*SUPERSAMPLE (default 4, at least 256) and downsamples with Lanczos.
"""
import os, subprocess, sys, tempfile
from PIL import Image

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

def render(svg_path, out_png, size, ss=4):
    big = max(256, size * ss)
    svg = open(svg_path, encoding="utf-8").read()
    html = ("<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}"
            f"svg{{display:block;width:{big}px;height:{big}px}}</style></head><body>{svg}</body></html>")
    with tempfile.TemporaryDirectory() as td:
        h = os.path.join(td, "x.html"); shot = os.path.join(td, "x.png")
        open(h, "w", encoding="utf-8").write(html)
        subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                        "--default-background-color=00000000", f"--window-size={big},{big}",
                        f"--screenshot={shot}", "file:///" + h.replace("\\", "/")],
                       check=True, capture_output=True, timeout=60)
        im = Image.open(shot).convert("RGBA").crop((0, 0, big, big))
        if big != size:
            im = im.resize((size, size), Image.LANCZOS)
        im.save(out_png)

if __name__ == "__main__":
    render(sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]) if len(sys.argv) > 4 else 4)
