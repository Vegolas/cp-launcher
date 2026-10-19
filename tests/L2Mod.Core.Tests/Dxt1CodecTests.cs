using L2Mod.Core.Imaging;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public class Dxt1CodecTests
{
    [Theory]
    [InlineData(32, 32, 512)]
    [InlineData(16, 16, 128)]
    [InlineData(2, 2, 8)]
    [InlineData(1, 1, 8)]
    public void GetDataSize_UsesFourByFourBlocks(int width, int height, int expected)
    {
        Assert.Equal(expected, Dxt1Codec.GetDataSize(width, height));
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 255, 255)]
    [InlineData(0, 0, 0)]
    public void RoundTrip_IsExactForRgb565Colours(int red, int green, int blue)
    {
        RgbaImage image = TestPackageBuilder.SolidImage(8, 8, (byte)red, (byte)green, (byte)blue);

        RgbaImage decoded = Dxt1Codec.Decode(Dxt1Codec.Encode(image), 8, 8);

        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void RoundTrip_KeepsTwoColourBlocksExact()
    {
        var image = new RgbaImage(4, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte value = 0;
                if (x >= 2)
                {
                    value = 255;
                }

                image.SetPixel(x, y, value, value, value, 255);
            }
        }

        RgbaImage decoded = Dxt1Codec.Decode(Dxt1Codec.Encode(image), 4, 4);

        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void RoundTrip_ApproximatesGradients()
    {
        var image = new RgbaImage(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                image.SetPixel(x, y, (byte)(x * 8), (byte)(y * 8), (byte)(255 - (x * 4)), 255);
            }
        }

        RgbaImage decoded = Dxt1Codec.Decode(Dxt1Codec.Encode(image), 32, 32);

        // A 2D gradient inside a 4x4 block cannot sit on DXT1's single colour line, so some error is inherent.
        // Bounds: mean error stays small, worst case stays below ~10% of the channel range.
        int maxError = 0;
        long totalError = 0;
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            int error = Math.Abs(image.Pixels[i] - decoded.Pixels[i]);
            maxError = Math.Max(maxError, error);
            totalError += error;
        }

        double meanError = (double)totalError / image.Pixels.Length;
        Assert.True(meanError <= 4.0, $"Mean channel error {meanError:F2} is too high.");
        Assert.True(maxError <= 24, $"Max channel error {maxError} is too high.");
    }

    [Fact]
    public void EncodeRegion_WithSeveralRegionsReencodesOnlyTheirBlocks()
    {
        RgbaImage original = TestPackageBuilder.NoiseImage(32, 32, 21);
        byte[] baseline = Dxt1Codec.Encode(original);
        RgbaImage edited = original.Clone();
        edited.FillRectangle(new PixelRect(0, 0, 8, 8), 255, 0, 0);
        edited.FillRectangle(new PixelRect(22, 22, 10, 10), 0, 0, 255);
        var regions = new[] { new PixelRect(0, 0, 8, 8), new PixelRect(22, 22, 10, 10), new PixelRect(4, 4, 4, 4) };

        byte[] encoded = Dxt1Codec.EncodeRegion(edited, baseline, regions);
        byte[] full = Dxt1Codec.Encode(edited);

        for (int block = 0; block < 64; block++)
        {
            int blockX = block % 8;
            int blockY = block / 8;
            bool inFirst = blockX <= 1 && blockY <= 1;
            bool inSecond = blockX >= 5 && blockY >= 5;
            byte[] expected = baseline.AsSpan(block * 8, 8).ToArray();
            if (inFirst || inSecond)
            {
                expected = full.AsSpan(block * 8, 8).ToArray();
            }

            Assert.Equal(expected, encoded.AsSpan(block * 8, 8).ToArray());
        }
    }

    [Fact]
    public void GetTouchedBlocks_ClipsRegionsToTheImage()
    {
        var regions = new[] { new PixelRect(-4, -4, 6, 6), new PixelRect(40, 40, 4, 4), new PixelRect(7, 0, 0, 4) };

        bool[] touched = Dxt1Codec.GetTouchedBlocks(16, 16, regions);

        Assert.Equal(16, touched.Length);
        Assert.True(touched[0]);
        Assert.Equal(1, touched.Count(flag => flag));
    }
}
