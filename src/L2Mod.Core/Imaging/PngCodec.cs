using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace L2Mod.Core.Imaging;

/// <summary>
/// Dependency-free PNG reader/writer. Reads 8-bit, non-interlaced truecolour (RGB/RGBA) and indexed images;
/// writes 8-bit RGBA. That covers what image editors save for icons.
/// </summary>
public static class PngCodec
{
    private const int ColorTypeRgb = 2;
    private const int ColorTypeIndexed = 3;
    private const int ColorTypeRgba = 6;

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static RgbaImage Load(string path)
    {
        return Decode(File.ReadAllBytes(path));
    }

    public static void Save(RgbaImage image, string path)
    {
        File.WriteAllBytes(path, Encode(image));
    }

    /// <summary>
    /// Width and height from the leading IHDR chunk, read without decoding, so untrusted files can be
    /// rejected before any pixel memory is allocated.
    /// </summary>
    public static (int Width, int Height) ReadSize(byte[] png)
    {
        const int HeaderEnd = 8 + 8 + 8;
        if (png.Length < HeaderEnd || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        if (Encoding.ASCII.GetString(png, Signature.Length + 4, 4) != "IHDR")
        {
            throw new InvalidDataException("PNG does not start with an IHDR chunk.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(Signature.Length + 8));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(Signature.Length + 12));
        return (width, height);
    }

    public static RgbaImage Decode(byte[] png)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0;
        int height = 0;
        int colorType = -1;
        byte[] palette = Array.Empty<byte>();
        byte[] paletteAlpha = Array.Empty<byte>();
        using var compressed = new MemoryStream();
        int position = Signature.Length;
        while (position + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            string type = Encoding.ASCII.GetString(png, position + 4, 4);
            int dataStart = position + 8;
            if (length < 0 || dataStart + length + 4 > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            ReadOnlySpan<byte> chunk = png.AsSpan(dataStart, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(4));
                int bitDepth = chunk[8];
                colorType = chunk[9];
                int interlace = chunk[12];
                if (bitDepth != 8 || interlace != 0)
                {
                    throw new NotSupportedException("Only 8-bit, non-interlaced PNG files are supported.");
                }

                if (colorType != ColorTypeRgb && colorType != ColorTypeRgba && colorType != ColorTypeIndexed)
                {
                    throw new NotSupportedException("Only RGB, RGBA and indexed PNG files are supported (save as 24/32-bit).");
                }
            }
            else if (type == "PLTE")
            {
                palette = chunk.ToArray();
            }
            else if (type == "tRNS")
            {
                paletteAlpha = chunk.ToArray();
            }
            else if (type == "IDAT")
            {
                compressed.Write(chunk);
            }
            else if (type == "IEND")
            {
                break;
            }

            position = dataStart + length + 4;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG has no IHDR chunk.");
        }

        int channels = GetChannels(colorType);
        byte[] raw = Inflate(compressed.ToArray());
        byte[] samples = Unfilter(raw, width, height, channels);
        return ToRgba(samples, width, height, colorType, palette, paletteAlpha);
    }

    public static byte[] Encode(RgbaImage image)
    {
        int stride = image.Width * 4;
        byte[] raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(image.Pixels, y * stride, raw, (y * (stride + 1)) + 1, stride);
        }

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = ColorTypeRgba;

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", Deflate(raw));
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static int GetChannels(int colorType)
    {
        if (colorType == ColorTypeRgb)
        {
            return 3;
        }

        if (colorType == ColorTypeRgba)
        {
            return 4;
        }

        return 1;
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        if (raw.Length < (stride + 1) * height)
        {
            throw new InvalidDataException("PNG image data is too short.");
        }

        byte[] result = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int source = (y * (stride + 1)) + 1;
            int target = y * stride;
            for (int x = 0; x < stride; x++)
            {
                int left = 0;
                if (x >= bytesPerPixel)
                {
                    left = result[target + x - bytesPerPixel];
                }

                int up = 0;
                if (y > 0)
                {
                    up = result[target - stride + x];
                }

                int upLeft = 0;
                if (y > 0 && x >= bytesPerPixel)
                {
                    upLeft = result[target - stride + x - bytesPerPixel];
                }

                int predictor;
                switch (filter)
                {
                    case 0:
                        predictor = 0;
                        break;
                    case 1:
                        predictor = left;
                        break;
                    case 2:
                        predictor = up;
                        break;
                    case 3:
                        predictor = (left + up) / 2;
                        break;
                    case 4:
                        predictor = Paeth(left, up, upLeft);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown PNG filter {filter}.");
                }

                result[target + x] = (byte)(raw[source + x] + predictor);
            }
        }

        return result;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int distanceLeft = Math.Abs(estimate - left);
        int distanceUp = Math.Abs(estimate - up);
        int distanceUpLeft = Math.Abs(estimate - upLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        if (distanceUp <= distanceUpLeft)
        {
            return up;
        }

        return upLeft;
    }

    private static RgbaImage ToRgba(byte[] samples, int width, int height, int colorType, byte[] palette, byte[] paletteAlpha)
    {
        var image = new RgbaImage(width, height);
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int target = i * 4;
            if (colorType == ColorTypeRgba)
            {
                Array.Copy(samples, i * 4, image.Pixels, target, 4);
            }
            else if (colorType == ColorTypeRgb)
            {
                Array.Copy(samples, i * 3, image.Pixels, target, 3);
                image.Pixels[target + 3] = 255;
            }
            else
            {
                int index = samples[i];
                if ((index * 3) + 2 >= palette.Length)
                {
                    throw new InvalidDataException("PNG palette index out of range.");
                }

                Array.Copy(palette, index * 3, image.Pixels, target, 3);
                image.Pixels[target + 3] = 255;
                if (index < paletteAlpha.Length)
                {
                    image.Pixels[target + 3] = paletteAlpha[index];
                }
            }
        }

        return image;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = UpdateCrc(0xFFFFFFFF, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint UpdateCrc(uint crc, byte[] data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0)
                {
                    c = 0xEDB88320 ^ (c >> 1);
                }
                else
                {
                    c >>= 1;
                }
            }

            table[n] = c;
        }

        return table;
    }
}
