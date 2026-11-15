#!/usr/bin/env python3
"""Redraws the README pictures:  python docs/render.py

Builds the app (dotnet build, no AOT needed) and runs its hidden `--render` mode, which draws the strip with the
app's own drawing code for made-up values: no window opens and no real sessions are read. The raw pixels are put
on a Windows 11 taskbar colour and saved as PNG. Needs the .NET SDK and `pip install pillow`.
"""
from __future__ import annotations

import os
import struct
import subprocess
import sys
import tempfile

from PIL import Image

DOCS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(DOCS)
TASKBAR = {'dark': (0x1c, 0x1c, 0x1c), 'light': (0xee, 0xee, 0xee)}
MIX = ['five=62', 'week=41', 'needsInput=1', 'planReady=1', 'working=3', 'done=12', 'idle=2']

# file name -> render arguments
SHOTS = {
    'strip-right.png': MIX,
    'strip-left.png': MIX + ['left'],
    'strip-light.png': MIX + ['light'],
    'statuses.png': ['needsInput=2', 'planReady=1', 'failed=1', 'working=3', 'done=14', 'idle=1'],
    'usage-high.png': ['five=93', 'week=74', 'working=1'],
    'usage-no-percent.png': ['five=62', 'week=41', 'nopct', 'working=2', 'done=3'],
    'usage-only.png': ['five=62', 'week=41'],
    'usage-reset.png': ['five=reset', 'week=41', 'done=2'],
}


def to_png(raw: str, out: str, light: bool) -> None:
    data = open(raw, 'rb').read()
    w, h = struct.unpack('<ii', data[:8])
    bg = TASKBAR['light' if light else 'dark']
    margin = h // 3
    img = Image.new('RGB', (w + 2 * margin, h), bg)
    px = img.load()
    for y in range(h):
        for x in range(w):
            b, g, r, a = data[8 + 4 * (y * w + x):12 + 4 * (y * w + x)]
            if a <= 1:  # alpha 1 = the invisible, clickable background
                continue
            k = 255 - a  # premultiplied: out = src + bg * (1 - alpha)
            px[x + margin, y] = (r + bg[0] * k // 255, g + bg[1] * k // 255, b + bg[2] * k // 255)
    img.save(out)


def main() -> int:
    wanted = sys.argv[1:] or list(SHOTS)
    with tempfile.TemporaryDirectory(prefix='taskbar-render-') as tmp:
        build = os.path.join(tmp, 'build')
        subprocess.run(['dotnet', 'build', os.path.join(REPO, 'src', 'ClaudeTaskbar.csproj'), '-c', 'Release', '-o', build,
                        '-v', 'quiet', '-nologo'], check=True)
        exe = os.path.join(build, 'ClaudeTaskbar.exe')
        for name in wanted:
            raw = os.path.join(tmp, name + '.bgra')
            subprocess.run([exe, '--render', raw, *SHOTS[name]], check=True)
            to_png(raw, os.path.join(DOCS, name), 'light' in SHOTS[name])
            print('wrote', os.path.join(DOCS, name))
    return 0


if __name__ == '__main__':
    sys.exit(main())
