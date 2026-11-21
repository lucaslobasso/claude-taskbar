# Makes the Claude Taskbar icon: Claude's app icon with the tile recoloured to blue; the starburst stays as is.
# Input: claude.exe's 256 px icon saved as PNG (e.g. PrivateExtractIcons at 256x256 from ~/.local/bin/claude.exe).
# Run from the repo root: python assets/make_icon.py claude.png src/app.ico 215 ; python assets/make_icon.py claude.png assets/icon.png 215
import sys, colorsys
from PIL import Image

src, out, hue = sys.argv[1], sys.argv[2], float(sys.argv[3]) / 360
sat_k = float(sys.argv[4]) if len(sys.argv) > 4 else 1.0
im = Image.open(src).convert('RGBA')
px = im.load()
for y in range(im.height):
    for x in range(im.width):
        r, g, b, a = px[x, y]
        if a == 0: continue
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        # starburst = bright coral; tile = dark. Blend over the anti-aliased edge (v 0.45..0.65).
        t = min(1, max(0, (v - 0.45) / 0.20))
        nr, ng, nb = colorsys.hsv_to_rgb(hue, min(1, s * sat_k), v)
        px[x, y] = (round(255 * (nr * (1 - t) + r / 255 * t)), round(255 * (ng * (1 - t) + g / 255 * t)),
                    round(255 * (nb * (1 - t) + b / 255 * t)), a)
if out.endswith('.ico'):
    im.save(out, sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
else:
    im.save(out)
