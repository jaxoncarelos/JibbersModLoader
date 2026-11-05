"""Deterministic PNG coverage fixtures; no game files or third-party assets are modified."""
from pathlib import Path
import json
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'mods/cs/AsyncContent/Fixtures'
OUTPUT.mkdir(parents=True, exist_ok=True)
manifest = []
PASSES = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]

def chunk(name, data):
    return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data))

def paeth(a, b, c):
    p = a + b - c
    errors = [abs(p-a), abs(p-b), abs(p-c)]
    return [a, b, c][errors.index(min(errors))]

def fixture(name, color, depth, channels, interlace=False, palette=None, transparency=None, gamma=False, width=7, height=5):
    maximum = (1 << depth)-1
    def sample(x, y, channel):
        if name.startswith('mip_'):
            # Nonlinear samples distinguish floor/nearest/ties-to-even averaging.
            value = ((x+1)*0x9e3779b1 ^ (y+13)*0x85ebca6b ^ (channel+17)*0xc2b2ae35) & 0xffffffff
            value ^= value >> 16
            value = value * 0x7feb352d & 0xffffffff
            value ^= value >> 15
            return value & maximum
        return ((x*37+y*61+channel*79)*257 + (173 if depth == 16 else 0)) & maximum
    samples = [[[sample(x, y, channel) for channel in range(channels)] for x in range(width)] for y in range(height)]
    expected = []
    for y in reversed(range(height)):
        for x in range(width):
            values = samples[y][x]
            if color == 3:
                index = values[0]
                rgba = list(palette[index*3:index*3+3]) + [transparency[index] if transparency and index < len(transparency) else 255]
            else:
                values = [v >> 8 if depth == 16 else v*255//maximum for v in values]
                rgba = ([values[0]]*3 + [values[1] if color == 4 else 255]) if color in (0, 4) else values[:3] + [values[3] if color == 6 else 255]
            expected.extend(rgba)
    scanlines = bytearray()
    for start_x, start_y, step_x, step_y in PASSES if interlace else [(0, 0, 1, 1)]:
        previous = None
        for y in range(start_y, height, step_y):
            pixels = [value for x in range(start_x, width, step_x) for value in samples[y][x]]
            if not pixels: continue
            if depth == 16: row = b''.join(struct.pack('>H', value) for value in pixels)
            elif depth == 8: row = bytes(pixels)
            else:
                row = bytearray((len(pixels)*depth+7)//8)
                for index, value in enumerate(pixels): row[index*depth//8] |= value << (8-depth-index*depth%8)
                row = bytes(row)
            if previous is None: previous = bytes(len(row))
            filter_type = y % 5
            stride = max(1, (channels*depth+7)//8)
            filtered = bytearray()
            for index, value in enumerate(row):
                left = row[index-stride] if index >= stride else 0
                above = previous[index]
                corner = previous[index-stride] if index >= stride else 0
                predicted = [0, left, above, (left+above)//2, paeth(left, above, corner)][filter_type]
                filtered.append((value-predicted) & 255)
            scanlines.append(filter_type)
            scanlines.extend(filtered)
            previous = row
    encoded = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, depth, color, 0, 0, int(interlace)))
    if palette: encoded += chunk(b'PLTE', palette)
    if transparency: encoded += chunk(b'tRNS', transparency)
    if gamma: encoded += chunk(b'gAMA', struct.pack('>I', 45455))
    encoded += chunk(b'IDAT', zlib.compress(scanlines)) + chunk(b'IEND', b'')
    (OUTPUT / (name + '.png')).write_bytes(encoded)
    manifest.append(dict(name=name, width=width, height=height, expected=expected))

fixture('rgb8', 2, 8, 3)
fixture('rgba8', 6, 8, 4)
fixture('gray1', 0, 1, 1)
fixture('gray2', 0, 2, 1)
fixture('gray4', 0, 4, 1)
fixture('gray8', 0, 8, 1)
fixture('gray_alpha8', 4, 8, 2)
fixture('rgb16', 2, 16, 3)
fixture('rgba16', 6, 16, 4)
fixture('gray16', 0, 16, 1)
fixture('gray_alpha16', 4, 16, 2)
palette = bytes([255, 0, 0, 0, 255, 0, 0, 0, 255, 51, 102, 153])
fixture('palette2_alpha', 3, 2, 1, palette=palette, transparency=bytes([255, 128, 0, 200]))
fixture('adam7_rgba8', 6, 8, 4, interlace=True)
fixture('adam7_gray2', 0, 2, 1, interlace=True)
fixture('gamma_rgba8', 6, 8, 4, gamma=True)
fixture('mip_rgb8_square', 2, 8, 3, width=16, height=16)
fixture('mip_rgb16_rectangle', 2, 16, 3, width=16, height=8)
fixture('mip_gray8_vertical', 0, 8, 1, width=1, height=8)
fixture('mip_gray8_horizontal', 0, 8, 1, width=8, height=1)
(OUTPUT / 'expected.json').write_text(json.dumps(manifest), encoding='utf-8')
print(f'Wrote {len(manifest)} PNG fixtures and independent expected pixels to {OUTPUT}')
