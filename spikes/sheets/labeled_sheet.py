"""Phase 0 helper: labeled review sheet (before | after | text) from PNG pairs.

Input: a TSV with columns  before_png  after_png  label  [mark_hex_color]
Usage: python labeled_sheet.py rows.tsv out.png [scale]
"""
import sys

from PIL import Image, ImageDraw, ImageFont


def load_font(size):
    for name in ("segoeui.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main():
    rows_path = sys.argv[1]
    out_path = sys.argv[2]
    scale = int(sys.argv[3]) if len(sys.argv) > 3 else 3
    rows = []
    with open(rows_path, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\n")
            if not line:
                continue
            rows.append(line.split("\t"))

    icon = 32 * scale
    pad = 8
    text_width = 520
    row_height = icon + pad
    font = load_font(15)
    sheet = Image.new("RGB", (pad * 4 + icon * 2 + text_width, pad + row_height * len(rows)), (34, 34, 38))
    draw = ImageDraw.Draw(sheet)
    for index, row in enumerate(rows):
        y = pad + index * row_height
        before = Image.open(row[0]).convert("RGB").resize((icon, icon), Image.NEAREST)
        after = Image.open(row[1]).convert("RGB").resize((icon, icon), Image.NEAREST)
        sheet.paste(before, (pad, y))
        sheet.paste(after, (pad * 2 + icon, y))
        x_text = pad * 3 + icon * 2
        label = row[2]
        if len(row) > 3 and row[3]:
            color = tuple(int(row[3].lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))
            draw.rectangle([x_text, y + 4, x_text + 14, y + 18], fill=color)
            x_text += 22
        draw.text((x_text, y + 2), f"{index + 1}. {label}", fill=(235, 235, 235), font=font)
    sheet.save(out_path)


if __name__ == "__main__":
    main()
