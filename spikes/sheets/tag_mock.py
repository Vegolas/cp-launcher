"""Phase 0 helper: mock of the planned `tag` operation (not the real encoder).

Draws an 8x8 tag in the top-left corner: 7x7 category colour plus a 1px black edge on the
sides facing the icon, i.e. at most 2 colours per 4x4 block, so the real DXT1 encode will be lossless.

Usage: python tag_mock.py in.png out.png RRGGBB [size]
"""
import sys

from PIL import Image


def main():
    source = Image.open(sys.argv[1]).convert("RGB")
    color = tuple(int(sys.argv[3].lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))
    size = int(sys.argv[4]) if len(sys.argv) > 4 else 8
    pixels = source.load()
    for y in range(size):
        for x in range(size):
            if x == size - 1 or y == size - 1:
                pixels[x, y] = (0, 0, 0)
            else:
                pixels[x, y] = color
    source.save(sys.argv[2])


if __name__ == "__main__":
    main()
