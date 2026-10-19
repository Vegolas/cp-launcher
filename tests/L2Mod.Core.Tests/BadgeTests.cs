using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public class BadgeTests
{
    private const byte IconKey = 0x38;

    [Theory]
    [InlineData(IconCorner.BottomRight, 16, 16)]
    [InlineData(IconCorner.BottomLeft, 0, 16)]
    [InlineData(IconCorner.TopRight, 16, 0)]
    [InlineData(IconCorner.TopLeft, 0, 0)]
    public void GetBadgeRect_PlacesBadgeInCorner(IconCorner corner, int expectedX, int expectedY)
    {
        PixelRect rect = BadgeComposer.GetBadgeRect(32, 32, new BadgeStyle(16, corner, 1));

        Assert.Equal(new PixelRect(expectedX, expectedY, 16, 16), rect);
    }

    [Fact]
    public void Compose_DrawsBorderAndBadgeOnlyInsideCorner()
    {
        RgbaImage icon = TestPackageBuilder.NoiseImage(32, 32, 7);
        RgbaImage badge = TestPackageBuilder.SolidImage(32, 32, 200, 40, 40);

        RgbaImage composed = BadgeComposer.Compose(icon, badge, BadgeStyle.Default);

        var rect = new PixelRect(16, 16, 16, 16);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int offset = composed.GetOffset(x, y);
                byte[] actual = composed.Pixels.AsSpan(offset, 4).ToArray();
                if (!rect.Contains(x, y))
                {
                    Assert.Equal(icon.Pixels.AsSpan(offset, 4).ToArray(), actual);
                }
                else if (x == 16 || y == 16 || x == 31 || y == 31)
                {
                    Assert.Equal(new byte[] { 0, 0, 0, 255 }, actual);
                }
                else
                {
                    Assert.Equal(new byte[] { 200, 40, 40, 255 }, actual);
                }
            }
        }
    }

    [Fact]
    public void Compose_WithCropUsesOnlyTheCentreOfTheBadgeIcon()
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(32, 32, 0, 0, 0);
        RgbaImage badge = TestPackageBuilder.SolidImage(32, 32, 255, 255, 255);
        badge.FillRectangle(new PixelRect(4, 4, 24, 24), 10, 200, 10);

        RgbaImage plain = BadgeComposer.Compose(icon, badge, BadgeStyle.Default);
        RgbaImage cropped = BadgeComposer.Compose(icon, badge, BadgeStyle.Default with { Crop = 24 });

        int corner = cropped.GetOffset(17, 17);
        Assert.Equal(new byte[] { 10, 200, 10, 255 }, cropped.Pixels.AsSpan(corner, 4).ToArray());
        Assert.NotEqual(new byte[] { 10, 200, 10, 255 }, plain.Pixels.AsSpan(corner, 4).ToArray());
    }

    [Fact]
    public void Crop_CopiesTheRequestedRectangle()
    {
        RgbaImage image = TestPackageBuilder.NoiseImage(8, 8, 5);

        RgbaImage crop = image.Crop(new PixelRect(2, 3, 4, 2));

        Assert.Equal(4, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(image.Pixels.AsSpan(image.GetOffset(2, 3), 16).ToArray(), crop.Pixels.AsSpan(0, 16).ToArray());
        Assert.Equal(image.Pixels.AsSpan(image.GetOffset(2, 4), 16).ToArray(), crop.Pixels.AsSpan(16, 16).ToArray());
        Assert.Throws<ArgumentException>(() => image.Crop(new PixelRect(6, 6, 4, 4)));
    }

    [Theory]
    [InlineData(2, 1, 0)]
    [InlineData(40, 1, 0)]
    [InlineData(8, 4, 0)]
    [InlineData(16, 1, 48)]
    public void Compose_RejectsInvalidStyles(int size, int border, int crop)
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(32, 32, 0, 0, 0);
        var style = new BadgeStyle(size, IconCorner.BottomRight, border, crop);

        Assert.Throws<ArgumentException>(() => BadgeComposer.Compose(icon, icon, style));
    }

    [Fact]
    public void Compose_UsesConfiguredBorderColour()
    {
        RgbaImage icon = TestPackageBuilder.SolidImage(32, 32, 0, 0, 0);
        RgbaImage badge = TestPackageBuilder.SolidImage(32, 32, 255, 255, 255);
        BadgeStyle style = BadgeStyle.Default with { BorderColor = Rgb.ParseHex("#C80000") };

        RgbaImage composed = BadgeComposer.Compose(icon, badge, style);

        Assert.Equal(new byte[] { 200, 0, 0, 255 }, composed.Pixels.AsSpan(composed.GetOffset(16, 16), 4).ToArray());
        Assert.Equal(new byte[] { 200, 0, 0, 255 }, composed.Pixels.AsSpan(composed.GetOffset(31, 20), 4).ToArray());
    }

    [Theory]
    [InlineData("00ff")]
    [InlineData("GGHHII")]
    public void ParseHex_RejectsInvalidColours(string value)
    {
        Assert.Throws<ArgumentException>(() => Rgb.ParseHex(value));
    }

    [Fact]
    public void ParseCorner_RejectsUnknownValue()
    {
        Assert.Throws<ArgumentException>(() => IconCorners.Parse("middle"));
    }

    [Fact]
    public void EncodeRegion_KeepsBlocksOutsideRegionByteIdentical()
    {
        RgbaImage original = TestPackageBuilder.NoiseImage(32, 32, 11);
        byte[] baseline = Dxt1Codec.Encode(original);
        RgbaImage edited = BadgeComposer.Compose(original, TestPackageBuilder.SolidImage(32, 32, 0, 255, 0), BadgeStyle.Default);

        byte[] encoded = Dxt1Codec.EncodeRegion(edited, baseline, new PixelRect(16, 16, 16, 16));
        byte[] full = Dxt1Codec.Encode(edited);

        for (int block = 0; block < 64; block++)
        {
            int blockX = block % 8;
            int blockY = block / 8;
            byte[] actual = encoded.AsSpan(block * 8, 8).ToArray();
            if (blockX >= 4 && blockY >= 4)
            {
                Assert.Equal(full.AsSpan(block * 8, 8).ToArray(), actual);
            }
            else
            {
                Assert.Equal(baseline.AsSpan(block * 8, 8).ToArray(), actual);
            }
        }
    }

    [Fact]
    public void BuildBatch_BadgesSeveralTexturesInOneFile()
    {
        IconPackage package = OpenSyntheticPackage();
        TextureData siren = package.FindTexture("skill0365");
        TextureData mystic = package.FindTexture("skill0273");
        RgbaImage sirenBefore = package.Decode(siren);
        RgbaImage sirenAfter = BadgeComposer.Compose(sirenBefore, package.Decode(package.FindTexture("skill1303")), BadgeStyle.Default);
        RgbaImage mysticAfter = BadgeComposer.Compose(package.Decode(mystic), package.Decode(package.FindTexture("skill1059")), BadgeStyle.Default);
        PixelRect region = BadgeComposer.GetBadgeRect(32, 32, BadgeStyle.Default);

        ReplacementResult result = package.BuildBatch(new[]
        {
            new TextureEdit(siren, sirenAfter, new[] { region }),
            new TextureEdit(mystic, mysticAfter, new[] { region }),
        });
        package.VerifyReplacement(result, new[] { "skill0365", "skill0273" });

        Assert.Equal(2, result.Patches.Count);
        var reopened = new IconPackage("Icon", L2File.FromBytes(result.FileBytes));
        RgbaImage decoded = reopened.Decode(reopened.FindTexture("skill0365"));
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                if (!region.Contains(x, y))
                {
                    int offset = decoded.GetOffset(x, y);
                    Assert.Equal(sirenBefore.Pixels.AsSpan(offset, 4).ToArray(), decoded.Pixels.AsSpan(offset, 4).ToArray());
                }
            }
        }

        RgbaImage untouched = reopened.Decode(reopened.FindTexture("skill1303"));
        Assert.Equal(package.Decode(package.FindTexture("skill1303")).Pixels, untouched.Pixels);
    }

    [Fact]
    public void BuildBatch_RejectsEditingTheSameTextureTwice()
    {
        IconPackage package = OpenSyntheticPackage();
        TextureData siren = package.FindTexture("skill0365");
        RgbaImage image = package.Decode(siren);

        Assert.Throws<InvalidOperationException>(() => package.BuildBatch(new[]
        {
            new TextureEdit(siren, image, null),
            new TextureEdit(siren, image, null),
        }));
    }

    private static IconPackage OpenSyntheticPackage()
    {
        byte[] plain = TestPackageBuilder.BuildPlain(
            new TestTexture("skill0365", TestPackageBuilder.NoiseImage(32, 32, 365), 1),
            new TestTexture("skill0273", TestPackageBuilder.NoiseImage(32, 32, 273), 1),
            new TestTexture("skill1303", TestPackageBuilder.SolidImage(32, 32, 220, 30, 30), 1),
            new TestTexture("skill1059", TestPackageBuilder.SolidImage(32, 32, 120, 120, 200), 1));
        return new IconPackage("Icon", L2File.FromBytes(TestPackageBuilder.Encrypt(plain, "121", IconKey)));
    }
}
