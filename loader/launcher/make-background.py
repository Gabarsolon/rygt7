# Recreates the look of the key art's backdrop (black, a coloured glow, thin light lines) from scratch.
# python make-background.py [blue|red]: blue (the default) or the earlier red.
# The release ships a 2560x1440 copy as background.png (resized with Lanczos).
import numpy as np, math, sys
from PIL import Image, ImageDraw, ImageFilter
W, H = 3840, 2160
y, x = np.mgrid[0:H, 0:W].astype(np.float32)
cx, cy = 2150, 800
d = np.sqrt(((x - cx) / 1.0) ** 2 + ((y - cy) / 1.1) ** 2)
glow = np.exp(-(d / 520.0) ** 2) * 0.85 + np.exp(-(d / 1100.0) ** 2) * 0.25
colours = {  # dark, glow, light lines
    'blue': ([1, 1, 10], [26, 34, 222], [60, 80, 255]),
    'red': ([7, 0, 0], [190, 22, 26], [255, 70, 60]),
}
dark, bright, light = colours[sys.argv[1] if len(sys.argv) > 1 else 'blue']
base = np.array(dark, np.float32)
peak = np.array(bright, np.float32)
img = base + glow[..., None] * (peak - base)
im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
lines = Image.new('L', (W, H), 0)
dr = ImageDraw.Draw(lines)
px, py = 1960, 560
for ang in (-160, -128, -75, -52, 20, 33, 62, 112):
    a = math.radians(ang)
    dr.line([(px, py), (px + 4000 * math.cos(a), py + 4000 * math.sin(a))], fill=255, width=3)
lines = lines.filter(ImageFilter.GaussianBlur(1.2))
la = np.asarray(lines, np.float32) / 255.0
fade = 0.18 + 0.55 * glow  # brighter near the glow, faint in the dark
arr = np.asarray(im, np.float32)
arr += (la * fade)[..., None] * (np.array(light, np.float32) - arr) * 0.5
noise = np.random.default_rng(1).normal(0, 1.2, (H, W, 1))  # dither against banding
Image.fromarray(np.clip(arr + noise, 0, 255).astype(np.uint8)).save('background-4k.png', optimize=True)
