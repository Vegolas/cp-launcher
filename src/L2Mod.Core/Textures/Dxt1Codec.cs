using System.Buffers.Binary;
using L2Mod.Core.Imaging;

namespace L2Mod.Core.Textures;

/// <summary>
/// DXT1 (BC1) codec. The encoder is a straightforward range fit along the principal colour axis and
/// always produces opaque 4-colour blocks; alpha in the source image is ignored.
/// </summary>
public static class Dxt1Codec
{
    private const int BlockSize = 8;
    private const int PowerIterations = 8;

    public static int GetDataSize(int width, int height)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        return blocksX * blocksY * BlockSize;
    }

    public static RgbaImage Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        if (data.Length != GetDataSize(width, height))
        {
            throw new ArgumentException($"DXT1 data for {width}x{height} must be {GetDataSize(width, height)} bytes.");
        }

        var image = new RgbaImage(width, height);
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        Span<byte> palette = stackalloc byte[16];
        for (int blockY = 0; blockY < blocksY; blockY++)
        {
            for (int blockX = 0; blockX < blocksX; blockX++)
            {
                ReadOnlySpan<byte> block = data.Slice(((blockY * blocksX) + blockX) * BlockSize, BlockSize);
                ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
                ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(2));
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4));
                BuildPalette(color0, color1, palette);
                for (int i = 0; i < 16; i++)
                {
                    int x = (blockX * 4) + (i % 4);
                    int y = (blockY * 4) + (i / 4);
                    if (x >= width || y >= height)
                    {
                        continue;
                    }

                    int index = (int)((indices >> (2 * i)) & 0x3);
                    image.SetPixel(x, y, palette[index * 4], palette[(index * 4) + 1], palette[(index * 4) + 2], palette[(index * 4) + 3]);
                }
            }
        }

        return image;
    }

    public static byte[] Encode(RgbaImage image)
    {
        int blocksX = Math.Max(1, (image.Width + 3) / 4);
        int blocksY = Math.Max(1, (image.Height + 3) / 4);
        byte[] output = new byte[blocksX * blocksY * BlockSize];
        for (int blockY = 0; blockY < blocksY; blockY++)
        {
            for (int blockX = 0; blockX < blocksX; blockX++)
            {
                Span<byte> block = output.AsSpan(((blockY * blocksX) + blockX) * BlockSize, BlockSize);
                EncodeBlock(image, blockX, blockY, block);
            }
        }

        return output;
    }

    /// <summary>
    /// Re-encodes only the 4x4 blocks that intersect <paramref name="region"/>; every other block is
    /// copied byte for byte from <paramref name="baseline"/>, so the rest of the texture loses nothing.
    /// </summary>
    public static byte[] EncodeRegion(RgbaImage image, ReadOnlySpan<byte> baseline, PixelRect region)
    {
        return EncodeRegion(image, baseline, new[] { region });
    }

    /// <summary>
    /// Re-encodes every 4x4 block that intersects any of <paramref name="regions"/>, each block once; every
    /// other block is copied byte for byte from <paramref name="baseline"/>. Parts of a region outside the
    /// image are ignored.
    /// </summary>
    public static byte[] EncodeRegion(RgbaImage image, ReadOnlySpan<byte> baseline, IReadOnlyList<PixelRect> regions)
    {
        if (baseline.Length != GetDataSize(image.Width, image.Height))
        {
            throw new ArgumentException("Baseline data does not match the image size.");
        }

        byte[] output = baseline.ToArray();
        int blocksX = Math.Max(1, (image.Width + 3) / 4);
        bool[] touched = GetTouchedBlocks(image.Width, image.Height, regions);
        for (int index = 0; index < touched.Length; index++)
        {
            if (!touched[index])
            {
                continue;
            }

            Span<byte> block = output.AsSpan(index * BlockSize, BlockSize);
            EncodeBlock(image, index % blocksX, index / blocksX, block);
        }

        return output;
    }

    /// <summary>One flag per 4x4 block (row-major): true when the block intersects any of the regions.</summary>
    public static bool[] GetTouchedBlocks(int width, int height, IReadOnlyList<PixelRect> regions)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        bool[] touched = new bool[blocksX * blocksY];
        var bounds = new PixelRect(0, 0, width, height);
        foreach (PixelRect region in regions)
        {
            PixelRect clipped = region.Intersect(bounds);
            if (clipped.IsEmpty)
            {
                continue;
            }

            for (int blockY = clipped.Y / 4; blockY <= (clipped.Bottom - 1) / 4; blockY++)
            {
                for (int blockX = clipped.X / 4; blockX <= (clipped.Right - 1) / 4; blockX++)
                {
                    touched[(blockY * blocksX) + blockX] = true;
                }
            }
        }

        return touched;
    }

    /// <summary>The nearest colour a DXT1 endpoint stores exactly (RGB565, expanded the way decoders do).</summary>
    public static Rgb QuantizeToRgb565(Rgb color)
    {
        Span<byte> expanded = stackalloc byte[4];
        ExpandRgb565(ToRgb565(color.Red, color.Green, color.Blue), expanded);
        return new Rgb(expanded[0], expanded[1], expanded[2]);
    }

    public static ushort ToRgb565(int red, int green, int blue)
    {
        int r = ((red * 31) + 127) / 255;
        int g = ((green * 63) + 127) / 255;
        int b = ((blue * 31) + 127) / 255;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static void EncodeBlock(RgbaImage image, int blockX, int blockY, Span<byte> output)
    {
        Span<int> red = stackalloc int[16];
        Span<int> green = stackalloc int[16];
        Span<int> blue = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int x = Math.Min((blockX * 4) + (i % 4), image.Width - 1);
            int y = Math.Min((blockY * 4) + (i / 4), image.Height - 1);
            int offset = image.GetOffset(x, y);
            red[i] = image.Pixels[offset];
            green[i] = image.Pixels[offset + 1];
            blue[i] = image.Pixels[offset + 2];
        }

        FindEndpoints(red, green, blue, out int maxIndex, out int minIndex);
        ushort color0 = ToRgb565(red[maxIndex], green[maxIndex], blue[maxIndex]);
        ushort color1 = ToRgb565(red[minIndex], green[minIndex], blue[minIndex]);
        if (color0 < color1)
        {
            (color0, color1) = (color1, color0);
        }

        uint indices = 0;
        if (color0 != color1)
        {
            Span<byte> palette = stackalloc byte[16];
            BuildPalette(color0, color1, palette);
            for (int i = 0; i < 16; i++)
            {
                int best = FindNearest(palette, red[i], green[i], blue[i]);
                indices |= (uint)best << (2 * i);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output, color0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(2), color1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4), indices);
    }

    private static void FindEndpoints(ReadOnlySpan<int> red, ReadOnlySpan<int> green, ReadOnlySpan<int> blue, out int maxIndex, out int minIndex)
    {
        double meanR = 0;
        double meanG = 0;
        double meanB = 0;
        for (int i = 0; i < 16; i++)
        {
            meanR += red[i];
            meanG += green[i];
            meanB += blue[i];
        }

        meanR /= 16;
        meanG /= 16;
        meanB /= 16;

        double rr = 0;
        double rg = 0;
        double rb = 0;
        double gg = 0;
        double gb = 0;
        double bb = 0;
        for (int i = 0; i < 16; i++)
        {
            double dr = red[i] - meanR;
            double dg = green[i] - meanG;
            double db = blue[i] - meanB;
            rr += dr * dr;
            rg += dr * dg;
            rb += dr * db;
            gg += dg * dg;
            gb += dg * db;
            bb += db * db;
        }

        double axisR = 1;
        double axisG = 1;
        double axisB = 1;
        for (int iteration = 0; iteration < PowerIterations; iteration++)
        {
            double nextR = (rr * axisR) + (rg * axisG) + (rb * axisB);
            double nextG = (rg * axisR) + (gg * axisG) + (gb * axisB);
            double nextB = (rb * axisR) + (gb * axisG) + (bb * axisB);
            double length = Math.Sqrt((nextR * nextR) + (nextG * nextG) + (nextB * nextB));
            if (length < 1e-9)
            {
                break;
            }

            axisR = nextR / length;
            axisG = nextG / length;
            axisB = nextB / length;
        }

        maxIndex = 0;
        minIndex = 0;
        double maxProjection = double.MinValue;
        double minProjection = double.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            double projection = (red[i] * axisR) + (green[i] * axisG) + (blue[i] * axisB);
            if (projection > maxProjection)
            {
                maxProjection = projection;
                maxIndex = i;
            }

            if (projection < minProjection)
            {
                minProjection = projection;
                minIndex = i;
            }
        }
    }

    private static int FindNearest(ReadOnlySpan<byte> palette, int red, int green, int blue)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < 4; index++)
        {
            int dr = palette[index * 4] - red;
            int dg = palette[(index * 4) + 1] - green;
            int db = palette[(index * 4) + 2] - blue;
            int distance = (dr * dr) + (dg * dg) + (db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    /// <summary>Fills 4 RGBA entries exactly as a DXT1 decoder does.</summary>
    private static void BuildPalette(ushort color0, ushort color1, Span<byte> palette)
    {
        ExpandRgb565(color0, palette.Slice(0, 4));
        ExpandRgb565(color1, palette.Slice(4, 4));
        if (color0 > color1)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)(((2 * palette[channel]) + palette[4 + channel]) / 3);
                palette[12 + channel] = (byte)((palette[channel] + (2 * palette[4 + channel])) / 3);
            }

            palette[11] = 255;
            palette[15] = 255;
        }
        else
        {
            for (int channel = 0; channel < 3; channel++)
            {
                palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
                palette[12 + channel] = 0;
            }

            palette[11] = 255;
            palette[15] = 0;
        }
    }

    private static void ExpandRgb565(ushort color, Span<byte> target)
    {
        int r = (color >> 11) & 0x1F;
        int g = (color >> 5) & 0x3F;
        int b = color & 0x1F;
        target[0] = (byte)((r << 3) | (r >> 2));
        target[1] = (byte)((g << 2) | (g >> 4));
        target[2] = (byte)((b << 3) | (b >> 2));
        target[3] = 255;
    }
}
