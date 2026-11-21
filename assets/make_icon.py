# Makes the Claude Taskbar icon: Claude's app icon with the tile recoloured to blue and the starburst in white (like
# Taskbar Glance's glyph: shaded slightly toward the tile's blue at the bottom), grown so the tile fills ~96% of the
# frame (Claude's own icon leaves ~8% padding on each side; most taskbar icons, e.g. Zen's, fill the frame).
# Input: claude.exe's 256 px icon saved as PNG (e.g. PrivateExtractIcons at 256x256 from ~/.local/bin/claude.exe).
# Run from the repo root: python assets/make_icon.py claude.png src/app.ico 215 ; python assets/make_icon.py claude.png assets/icon.png 215
import sys, colorsys
from PIL import Image

src, out, hue = sys.argv[1], sys.argv[2], float(sys.argv[3]) / 360
sat_k = float(sys.argv[4]) if len(sys.argv) > 4 else 1.0
FILL = 0.96                 # tile size relative to the frame
GLYPH = (255, 255, 255)     # starburst colour
SHADOW = (190, 205, 228)    # starburst at its darkest: white pulled toward the tile (same as Taskbar Glance)

im = Image.open(src).convert('RGBA')
px = im.load()
# the starburst's own shading (lighter at the top, darker at the bottom) becomes white -> SHADOW
vals = [colorsys.rgb_to_hsv(*(c / 255 for c in px[x, y][:3]))[2] for y in range(im.height) for x in range(im.width)
        if px[x, y][3] and colorsys.rgb_to_hsv(*(c / 255 for c in px[x, y][:3]))[2] >= 0.65]
vlo, vhi = min(vals), max(vals)
for y in range(im.height):
    for x in range(im.width):
        r, g, b, a = px[x, y]
        if a == 0: continue
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        # starburst = bright coral; tile = dark. Blend over the anti-aliased edge (v 0.45..0.65).
        t = min(1, max(0, (v - 0.45) / 0.20))
        tr, tg, tb = colorsys.hsv_to_rgb(hue, min(1, s * sat_k), v)
        d = min(1, max(0, (vhi - v) / max(1e-6, vhi - vlo)))
        gr, gg, gb = (GLYPH[i] + (SHADOW[i] - GLYPH[i]) * d for i in range(3))
        px[x, y] = (round(255 * tr * (1 - t) + gr * t), round(255 * tg * (1 - t) + gg * t),
                    round(255 * tb * (1 - t) + gb * t), a)

bb = im.getbbox()
tile = im.crop(bb)
side = round(256 * FILL)
tile = tile.resize((side, round(side * tile.height / tile.width)), Image.LANCZOS)
im = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
im.alpha_composite(tile, ((256 - tile.width) // 2, (256 - tile.height) // 2))
if out.endswith('.ico'):
    im.save(out, sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
else:
    im.save(out)
