"""Phase 0 helper: grouped grid sheet of icons with a mock category tag (see tag_mock.py).

Usage: python grid_sheet.py mapping.txt png_dir out.png
mapping lines: <texture> <CATEGORY>  # label ; png_dir holds <texture>.png exports of the originals.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

COLORS = {"CC": (0xE0, 0x20, 0x20), "DOT": (0xA0, 0x40, 0xFF), "STAT": (0xFF, 0x99, 0x00)}
TITLES = {"CC": "CC (stun / sleep / hold / fear / silence)", "DOT": "DOT (trucizna / krwawienie / podpalenie / drenaż)", "STAT": "STAT (obniżone statystyki / prędkość / odporności)"}
SCALE = 3
COLUMNS = 7
CELL_W = 170
CELL_H = 32 * SCALE + 44
TAG = 8


def load_font(size):
    for name in ("segoeui.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def parse(path):
    entries = []
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            body, _, label = line.partition("#")
            texture, category = body.split()
            entries.append((texture, category, label.strip()))
    return entries


def tagged(path, color):
    icon = Image.open(path).convert("RGB")
    pixels = icon.load()
    for y in range(TAG):
        for x in range(TAG):
            if x == TAG - 1 or y == TAG - 1:
                pixels[x, y] = (0, 0, 0)
            else:
                pixels[x, y] = color
    return icon.resize((32 * SCALE, 32 * SCALE), Image.NEAREST)


def main():
    entries = parse(sys.argv[1])
    png_dir = sys.argv[2]
    font = load_font(13)
    header_font = load_font(18)
    groups = [(category, [e for e in entries if e[1] == category]) for category in ("CC", "DOT", "STAT")]
    height = 8
    for _, items in groups:
        height += 34 + ((len(items) + COLUMNS - 1) // COLUMNS) * CELL_H
    sheet = Image.new("RGB", (COLUMNS * CELL_W + 16, height), (34, 34, 38))
    draw = ImageDraw.Draw(sheet)
    y = 8
    number = 0
    for category, items in groups:
        draw.rectangle([8, y + 6, 26, y + 24], fill=COLORS[category])
        draw.text((34, y + 2), f"{TITLES[category]} - {len(items)}", fill=(240, 240, 240), font=header_font)
        y += 34
        for index, (texture, _, label) in enumerate(items):
            number += 1
            x = 8 + (index % COLUMNS) * CELL_W
            cy = y + (index // COLUMNS) * CELL_H
            sheet.paste(tagged(os.path.join(png_dir, texture + ".png"), COLORS[category]), (x + (CELL_W - 32 * SCALE) // 2 - 4, cy))
            name = label.split(" (")[0]
            draw.text((x, cy + 32 * SCALE + 2), f"{number}. {texture.replace('skill', '')}", fill=(170, 170, 170), font=font)
            draw.text((x, cy + 32 * SCALE + 19), name[:24], fill=(240, 240, 240), font=font)
        y += ((len(items) + COLUMNS - 1) // COLUMNS) * CELL_H
    sheet.save(sys.argv[3])


if __name__ == "__main__":
    main()
