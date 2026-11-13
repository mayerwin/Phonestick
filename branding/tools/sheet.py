"""Preview sheet: py -3 sheet.py in.svg out.png  (256, 64, 32, 24, 16 px on dark and light)."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from render import render
from PIL import Image
svg, out = sys.argv[1], sys.argv[2]
sizes = [256, 64, 32, 24, 16]
tmp = out + ".parts"
os.makedirs(tmp, exist_ok=True)
ims = []
for s in sizes:
    p = os.path.join(tmp, f"{s}.png"); render(svg, p, s); ims.append(Image.open(p))
W = sum(sizes) + 20 * (len(sizes) + 1)
sheet = Image.new("RGBA", (W, 2 * 276), (32, 32, 32, 255))
sheet.paste(Image.new("RGBA", (W, 276), (243, 243, 243, 255)), (0, 276))
for row in (0, 276):
    x = 20
    for s, im in zip(sizes, ims):
        sheet.paste(im, (x, row + 10), im); x += s + 20
# magnified small sizes (nearest) so pixels are visible
mag = Image.new("RGBA", (3 * 16 * 8 + 80, 16 * 8 + 20), (32, 32, 32, 255))
x = 10
for s, im in zip(sizes, ims):
    if s in (32, 24, 16):
        z = im.resize((s * 4, s * 4), Image.NEAREST); mag.paste(z, (x, 10), z); x += s * 4 + 20
final = Image.new("RGBA", (max(W, mag.width), sheet.height + mag.height), (32, 32, 32, 255))
final.paste(sheet, (0, 0)); final.paste(mag, (0, sheet.height))
final.save(out)
