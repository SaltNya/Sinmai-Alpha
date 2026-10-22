"""Generate deterministic GIF fixtures and independent Pillow reference frames."""
import json
import random
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageSequence

root = Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
palette = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255] + [0] * (768 - 12)
manifest = []

def record(name, frames, **options):
    path = root / (name + '.gif')
    frames[0].save(path, save_all=True, append_images=frames[1:], **options)
    result = {'file': path.name, 'frames': []}
    with Image.open(path) as gif:
        result['width'], result['height'] = gif.size
        for i, frame in enumerate(ImageSequence.Iterator(gif)):
            rgba = frame.convert('RGBA').transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes()
            expected = f'{name}-{i}.rgba'
            (root / expected).write_bytes(rgba)
            result['frames'].append({'pixels': expected, 'delay': max(.02, frame.info.get('duration', 0) / 1000 or .1)})
    manifest.append(result)

frames = []
for i in range(3):
    im = Image.new('P', (32, 24), 0)
    im.putpalette(palette)
    ImageDraw.Draw(im).rectangle((i * 8, i * 5, i * 8 + 7, i * 5 + 7), fill=i + 1)
    frames.append(im)
record('transparent-disposal2', frames, transparency=0, disposal=2, duration=[80, 130, 210], loop=0, optimize=True)
record('restore-disposal3', frames, transparency=0, disposal=[1, 3, 1], duration=[40, 70, 110], loop=1, optimize=False)
record('keep-disposal1', frames, transparency=0, disposal=1, duration=[0, 10, 200], optimize=True)
rng = random.Random(94123)
noise = Image.new('P', (129, 79))
noise.putpalette([rng.randrange(256) for _ in range(768)])
noise.putdata([rng.randrange(256) for _ in range(129 * 79)])
record('interlaced-lzw12', [noise], interlace=True)
record('local-palette', [Image.new('RGB', (25, 23), c) for c in ['red', 'green', 'blue']], duration=[100, 200, 300], loop=0)
# Runtime smoke test: asymmetrical corners expose vertical flips; a second color
# fills formerly transparent pixels so a Tight sprite mesh would be observable.
runtime = []
for i in range(3):
    im = Image.new('P', (96, 96), 0); im.putpalette(palette)
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, 95, 23), fill=1)
    d.rectangle((0, 72, 95, 95), fill=3)
    if i: d.rectangle((24, 24, 71, 71), fill=i)
    runtime.append(im)
record('jacket-smoke', runtime, transparency=0, disposal=2, duration=[200, 300, 400], loop=0)
# Independent PNG fallback fixture.
Image.new('RGBA', (64, 64), (230, 160, 20, 255)).save(root / 'fallback.png')
(root / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(f'Generated {len(manifest)} GIF fixtures in {root}')
