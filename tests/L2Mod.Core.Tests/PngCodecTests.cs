using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using L2Mod.Core.Imaging;

namespace L2Mod.Core.Tests;

public class PngCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsRgba()
    {
        var image = new RgbaImage(5, 3);
        var random = new Random(1235);
        random.NextBytes(image.Pixels);

        RgbaImage decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(5, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_HandlesAllRowFiltersForRgb()
    {
        const int width = 6;
        const int height = 5;
        var random = new Random(42);
        byte[] rgb = new byte[width * height * 3];
        random.NextBytes(rgb);

        byte[] png = BuildFilteredRgbPng(rgb, width, height);
        RgbaImage decoded = PngCodec.Decode(png);

        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(rgb[i * 3], decoded.Pixels[i * 4]);
            Assert.Equal(rgb[(i * 3) + 1], decoded.Pixels[(i * 4) + 1]);
            Assert.Equal(rgb[(i * 3) + 2], decoded.Pixels[(i * 4) + 2]);
            Assert.Equal(255, decoded.Pixels[(i * 4) + 3]);
        }
    }

    [Fact]
    public void Decode_RejectsNonPng()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    /// <summary>Encodes each row with a different filter (0..4) so the decoder's unfiltering is exercised.</summary>
    private static byte[] BuildFilteredRgbPng(byte[] rgb, int width, int height)
    {
        const int bpp = 3;
        int stride = width * bpp;
        byte[] raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int filter = y % 5;
            raw[y * (stride + 1)] = (byte)filter;
            for (int x = 0; x < stride; x++)
            {
                int left = 0;
                if (x >= bpp)
                {
                    left = rgb[(y * stride) + x - bpp];
                }

                int up = 0;
                if (y > 0)
                {
                    up = rgb[((y - 1) * stride) + x];
                }

                int upLeft = 0;
                if (y > 0 && x >= bpp)
                {
                    upLeft = rgb[((y - 1) * stride) + x - bpp];
                }

                int predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };

                raw[(y * (stride + 1)) + 1 + x] = (byte)(rgb[(y * stride) + x] - predictor);
            }
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
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

    /// <summary>The decoder ignores CRCs, so a zero CRC is fine here.</summary>
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]);
    }
}
