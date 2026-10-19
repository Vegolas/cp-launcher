using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Operations;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public class OperationTests
{
    private const string Icon = "Icon.utx";

    [Theory]
    [InlineData(IconCorner.TopLeft, 0, 0, 7, 7)]
    [InlineData(IconCorner.TopRight, 24, 0, 24, 7)]
    [InlineData(IconCorner.BottomLeft, 0, 24, 7, 24)]
    [InlineData(IconCorner.BottomRight, 24, 24, 24, 24)]
    public void TagComposer_DrawsBlackEdgeOnTheSidesFacingTheIcon(IconCorner corner, int x, int y, int edgeX, int edgeY)
    {
        RgbaImage icon = TestPackageBuilder.NoiseImage(32, 32, 1);
        RgbaImage original = icon.Clone();
        Rgb color = Dxt1Codec.QuantizeToRgb565(Rgb.ParseHex("#E02020"));

        PixelRect rect = TagComposer.Draw(icon, Rgb.ParseHex("#E02020"), 8, corner);

        Assert.Equal(new PixelRect(x, y, 8, 8), rect);
        for (int py = 0; py < 32; py++)
        {
            for (int px = 0; px < 32; px++)
            {
                byte[] actual = TestIcons.Pixel(icon, px, py);
                if (!rect.Contains(px, py))
                {
                    Assert.Equal(TestIcons.Pixel(original, px, py), actual);
                }
                else if (px == edgeX || py == edgeY)
                {
                    Assert.Equal(new byte[] { 0, 0, 0, 255 }, actual);
                }
                else
                {
                    Assert.Equal(new byte[] { color.Red, color.Green, color.Blue, 255 }, actual);
                }
            }
        }
    }

    [Theory]
    [InlineData("#E02020")]
    [InlineData("#A040FF")]
    [InlineData("#FF9900")]
    public void Tag_IsLosslessAndKeepsBlocksOutsideTheTagByteIdentical(string color)
    {
        IconPackage package = TestIcons.OpenDefault();
        var tag = new TagOperation(Icon, "skill0102", Rgb.ParseHex(color), 8, IconCorner.TopLeft);

        OperationBuildResult build = OperationComposer.Build(package, new[] { tag }, null);

        IconPackage patched = TestIcons.Open(build.Replacement.FileBytes);
        byte[] before = TestIcons.Mip0(package, "skill0102");
        byte[] after = TestIcons.Mip0(patched, "skill0102");
        RgbaImage expected = build.Textures[0].After;
        RgbaImage decoded = patched.Decode(patched.FindTexture("skill0102"));
        var tagRect = new PixelRect(0, 0, 8, 8);
        for (int block = 0; block < 64; block++)
        {
            var blockRect = new PixelRect((block % 8) * 4, (block / 8) * 4, 4, 4);
            if (!blockRect.Intersects(tagRect))
            {
                Assert.Equal(before.AsSpan(block * 8, 8).ToArray(), after.AsSpan(block * 8, 8).ToArray());
                continue;
            }

            for (int y = blockRect.Y; y < blockRect.Bottom; y++)
            {
                for (int x = blockRect.X; x < blockRect.Right; x++)
                {
                    Assert.Equal(TestIcons.Pixel(expected, x, y), TestIcons.Pixel(decoded, x, y));
                }
            }
        }

        Assert.Empty(build.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(36)]
    public void TagComposer_RejectsSizesThatAreNotWholeBlocks(int size)
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(32, 32, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => TagComposer.Draw(icon, Rgb.Black, size, IconCorner.TopLeft));
    }

    [Fact]
    public void QuantizeToRgb565_GivesColoursDxt1StoresExactly()
    {
        Rgb color = Dxt1Codec.QuantizeToRgb565(Rgb.ParseHex("#E02020"));
        RgbaImage image = TestPackageBuilder.SolidImage(8, 8, color.Red, color.Green, color.Blue);

        RgbaImage decoded = Dxt1Codec.Decode(Dxt1Codec.Encode(image), 8, 8);

        Assert.Equal(image.Pixels, decoded.Pixels);
        Assert.Equal(color, Dxt1Codec.QuantizeToRgb565(color));
    }

    [Fact]
    public void OverlayComposer_BlendsAlphaAndReturnsBoundsOfVisiblePixels()
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(32, 32, 100, 100, 100);
        var overlay = new RgbaImage(16, 16);
        overlay.SetPixel(14, 14, 255, 0, 0, 255);
        overlay.SetPixel(15, 15, 255, 255, 255, 128);

        PixelRect? rect = OverlayComposer.Draw(icon, overlay, IconCorner.BottomRight);

        Assert.Equal(new PixelRect(30, 30, 2, 2), rect);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, TestIcons.Pixel(icon, 30, 30));
        Assert.Equal(new byte[] { 178, 178, 178, 255 }, TestIcons.Pixel(icon, 31, 31));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, TestIcons.Pixel(icon, 31, 30));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, TestIcons.Pixel(icon, 16, 16));
    }

    [Fact]
    public void OverlayComposer_FullyTransparentOverlayChangesNothing()
    {
        RgbaImage icon = TestPackageBuilder.NoiseImage(32, 32, 4);
        RgbaImage original = icon.Clone();

        PixelRect? rect = OverlayComposer.Draw(icon, new RgbaImage(8, 8), IconCorner.TopLeft);

        Assert.Null(rect);
        Assert.Equal(original.Pixels, icon.Pixels);
    }

    [Fact]
    public void OverlayComposer_RejectsOverlayLargerThanTheIcon()
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(16, 16, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => OverlayComposer.Draw(icon, new RgbaImage(32, 32), IconCorner.TopLeft));
    }

    [Fact]
    public void OverlayImages_AcceptsSmallPngWithMatchingHash()
    {
        byte[] png = PngCodec.Encode(TestPackageBuilder.SolidImage(32, 32, 1, 2, 3));

        RgbaImage image = OverlayImages.Decode(png, ContentHash.Sha256Hex(png));

        Assert.Equal(32, image.Width);
    }

    [Fact]
    public void OverlayImages_RejectsWrongHash()
    {
        byte[] png = PngCodec.Encode(TestPackageBuilder.SolidImage(8, 8, 1, 2, 3));

        Assert.Throws<InvalidDataException>(() => OverlayImages.Decode(png, new string('0', 64)));
    }

    [Fact]
    public void OverlayImages_RejectsImagesOver32Pixels()
    {
        byte[] png = PngCodec.Encode(TestPackageBuilder.SolidImage(33, 8, 1, 2, 3));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => OverlayImages.Decode(png, ContentHash.Sha256Hex(png)));

        Assert.Contains("33x8", exception.Message);
    }

    [Fact]
    public void OverlayImages_RejectsFilesOver64Kilobytes()
    {
        byte[] png = new byte[(64 * 1024) + 1];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => OverlayImages.Decode(png, ContentHash.Sha256Hex(png)));

        Assert.Contains("limit", exception.Message);
    }

    [Fact]
    public void OverlayImages_ChecksTheHeaderBeforeAllocatingPixels()
    {
        byte[] png = PngCodec.Encode(TestPackageBuilder.SolidImage(1, 1, 0, 0, 0));
        png[16] = 0x00;
        png[17] = 0x01;
        png[20] = 0x00;
        png[21] = 0x01;

        Assert.Equal((65536 + 1, 65536 + 1), PngCodec.ReadSize(png));
        Assert.Throws<InvalidDataException>(() => OverlayImages.Decode(png, ContentHash.Sha256Hex(png)));
    }

    [Fact]
    public void Build_ComposesSeveralOperationsOnOneTextureAndEncodesItOnce()
    {
        IconPackage package = TestIcons.OpenDefault();
        var operations = new IIconOperation[]
        {
            new TagOperation(Icon, "skill0102", Rgb.ParseHex("#E02020"), 8, IconCorner.TopLeft),
            new BadgeOperation(Icon, "skill0102", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 1)),
        };

        OperationBuildResult build = OperationComposer.Build(package, operations, null);

        Assert.Single(build.Textures);
        Assert.Single(build.Replacement.Patches);
        Assert.Equal(new[] { new PixelRect(0, 0, 8, 8), new PixelRect(12, 12, 20, 20) }, build.Textures[0].Regions);
        Assert.Empty(build.Warnings);
        IconPackage patched = TestIcons.Open(build.Replacement.FileBytes);
        RgbaImage decoded = patched.Decode(patched.FindTexture("skill0102"));
        Rgb tag = Dxt1Codec.QuantizeToRgb565(Rgb.ParseHex("#E02020"));
        Assert.Equal(new byte[] { tag.Red, tag.Green, tag.Blue, 255 }, TestIcons.Pixel(decoded, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, TestIcons.Pixel(decoded, 12, 12));
        byte[] before = TestIcons.Mip0(package, "skill0102");
        byte[] after = TestIcons.Mip0(patched, "skill0102");
        bool[] touched = Dxt1Codec.GetTouchedBlocks(32, 32, build.Textures[0].Regions);
        for (int block = 0; block < 64; block++)
        {
            if (!touched[block])
            {
                Assert.Equal(before.AsSpan(block * 8, 8).ToArray(), after.AsSpan(block * 8, 8).ToArray());
            }
        }
    }

    [Fact]
    public void Build_BadgeOperationMatchesDirectBadgeComposition()
    {
        IconPackage package = TestIcons.OpenDefault();
        var style = new BadgeStyle(20, IconCorner.BottomRight, 1);
        TextureData target = package.FindTexture("skill0365");
        RgbaImage composed = BadgeComposer.Compose(package.Decode(target), package.Decode(package.FindTexture("skill1303")), style);
        ReplacementResult direct = package.BuildBatch(new[] { new TextureEdit(target, composed, new[] { BadgeComposer.GetBadgeRect(32, 32, style) }) });

        OperationBuildResult build = OperationComposer.Build(package, new[] { new BadgeOperation(Icon, "skill0365", "skill1303", style) }, null);

        Assert.Equal(direct.FileBytes, build.Replacement.FileBytes);
    }

    [Fact]
    public void Build_WarnsWhenTwoOperationsShareACorner()
    {
        IconPackage package = TestIcons.OpenDefault();
        var operations = new IIconOperation[]
        {
            new TagOperation(Icon, "skill0102", Rgb.ParseHex("#E02020"), 8, IconCorner.TopLeft),
            new TagOperation(Icon, "skill_i.skill0102", Rgb.ParseHex("#A040FF"), 4, IconCorner.TopLeft),
        };

        OperationBuildResult build = OperationComposer.Build(package, operations, null);

        Assert.Single(build.Textures);
        Assert.Contains(build.Warnings, warning => warning.Contains("both use corner tl"));
    }

    [Fact]
    public void Build_WarnsWhenOperationsInDifferentCornersOverlap()
    {
        IconPackage package = TestIcons.OpenDefault();
        var operations = new IIconOperation[]
        {
            new TagOperation(Icon, "skill0102", Rgb.ParseHex("#E02020"), 16, IconCorner.TopLeft),
            new BadgeOperation(Icon, "skill0102", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 1)),
        };

        OperationBuildResult build = OperationComposer.Build(package, operations, null);

        Assert.Contains(build.Warnings, warning => warning.Contains("overlaps"));
    }

    [Fact]
    public void Build_RejectsOperationsForAnotherFile()
    {
        IconPackage package = TestIcons.OpenDefault();
        var tag = new TagOperation("Icon_c.utx", "skill0102", Rgb.Black, 8, IconCorner.TopLeft);

        Assert.Throws<ArgumentException>(() => OperationComposer.Build(package, new[] { tag }, null));
    }

    [Fact]
    public void Build_OverlayReadsItsImageFromTheAssetStore()
    {
        IconPackage package = TestIcons.OpenDefault();
        var mark = new RgbaImage(8, 8);
        mark.FillRectangle(new PixelRect(0, 0, 8, 8), 0, 255, 0);
        byte[] png = PngCodec.Encode(mark);
        var assets = new InMemoryAssetStore();
        string path = assets.Add(png);
        var overlay = new OverlayOperation(Icon, "skill0365", path, ContentHash.Sha256Hex(png), IconCorner.BottomRight);

        OperationBuildResult build = OperationComposer.Build(package, new[] { overlay }, assets);

        IconPackage patched = TestIcons.Open(build.Replacement.FileBytes);
        RgbaImage decoded = patched.Decode(patched.FindTexture("skill0365"));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, TestIcons.Pixel(decoded, 28, 28));
        Assert.Equal(new[] { new PixelRect(24, 24, 8, 8) }, build.Textures[0].Regions);
        Assert.Throws<InvalidOperationException>(() => OperationComposer.Build(package, new[] { overlay }, null));
    }
}
