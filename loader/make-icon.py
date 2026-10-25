"""Rebuild the original ryGT7 project icon (Pillow required; no game artwork)."""
from pathlib import Path
from PIL import Image, ImageDraw

scale = 4
image = Image.new('RGBA', (256 * scale, 256 * scale))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 256 * scale - 1, 256 * scale - 1), radius=48 * scale, fill='#121922')
mask = image.getchannel('A')
def polygon(points, color):
    draw.polygon([(x * scale, y * scale) for x, y in points], fill=color)
polygon([(0,186),(186,0),(256,0),(256,70),(70,256),(0,256)], '#134f88')
polygon([(0,230),(230,0),(256,0),(256,26),(26,256),(0,256)], '#c82c42')
polygon([(60,54),(203,54),(203,91),(127,205),(79,205),(157,93),(60,93)], '#ffffff')
image.putalpha(mask)
image.resize((256,256), Image.Resampling.LANCZOS).save(Path(__file__).with_name('ryGT7.ico'),
    sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
