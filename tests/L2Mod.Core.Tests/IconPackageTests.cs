using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public class IconPackageTests
{
    private const byte IconKey = 0x38;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void FindTexture_LocatesMipsBehindLicenseeData(int mipCount)
    {
        IconPackage package = OpenSyntheticIcon(mipCount);

        TextureData texture = package.FindTexture("skill1235");

        Assert.Equal("skill_i.skill1235", texture.Path);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(mipCount, texture.Mips.Count);
        Assert.Equal(32, texture.Mips[0].DataSize);
    }

    [Theory]
    [InlineData("skill1235")]
    [InlineData("skill_i.skill1235")]
    [InlineData("icon.skill1235")]
    [InlineData("Icon.skill_i.skill1235")]
    public void FindTexture_AcceptsSkillgrpStyleNames(string name)
    {
        IconPackage package = OpenSyntheticIcon(1);

        TextureData texture = package.FindTexture(name);

        Assert.Equal("skill_i.skill1235", texture.Path);
    }

    [Theory]
    [InlineData("skill9999")]
    [InlineData("skill_i")]
    [InlineData("other.skill1235")]
    [InlineData("1235")]
    public void FindTexture_ThrowsForUnknownTexture(string name)
    {
        IconPackage package = OpenSyntheticIcon(1);

        Assert.Throws<KeyNotFoundException>(() => package.FindTexture(name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void BuildReplacement_PatchesInPlaceAndKeepsEncryption(int mipCount)
    {
        IconPackage package = OpenSyntheticIcon(mipCount);
        TextureData texture = package.FindTexture("skill1235");
        RgbaImage red = TestPackageBuilder.SolidImage(8, 8, 255, 0, 0);

        ReplacementResult result = package.BuildReplacement(texture, red);
        package.VerifyReplacement(result, new[] { "skill1235" });

        Assert.Equal(package.SourceFile.RawBytes.Length, result.FileBytes.Length);
        Assert.True(result.ChangedBytes > 0);
        Assert.Equal(mipCount, result.Patches.Count);
        Assert.Equal(package.SourceFile.RawBytes.AsSpan(0, L2File.HeaderSize).ToArray(), result.FileBytes.AsSpan(0, L2File.HeaderSize).ToArray());

        var reopened = new IconPackage("Icon", L2File.FromBytes(result.FileBytes));
        RgbaImage decoded = reopened.Decode(reopened.FindTexture("skill1235"));
        Assert.Equal(red.Pixels, decoded.Pixels);
        Assert.Equal(L2Encryption.Ver121, reopened.SourceFile.Encryption);
        Assert.Equal(IconKey, reopened.SourceFile.Key);
    }

    [Fact]
    public void BuildReplacement_DownscalesIntegerMultiples()
    {
        IconPackage package = OpenSyntheticIcon(1);
        TextureData texture = package.FindTexture("skill1235");
        RgbaImage green = TestPackageBuilder.SolidImage(32, 32, 0, 255, 0);

        ReplacementResult result = package.BuildReplacement(texture, green);

        Assert.Contains(result.Warnings, warning => warning.Contains("downscaled"));
    }

    [Fact]
    public void BuildReplacement_RejectsWrongSize()
    {
        IconPackage package = OpenSyntheticIcon(1);
        TextureData texture = package.FindTexture("skill1235");
        RgbaImage wrong = TestPackageBuilder.SolidImage(12, 12, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => package.BuildReplacement(texture, wrong));
    }

    private static IconPackage OpenSyntheticIcon(int mipCount)
    {
        RgbaImage blue = TestPackageBuilder.SolidImage(8, 8, 0, 0, 255);
        byte[] plain = TestPackageBuilder.BuildPlain(blue, mipCount);
        byte[] encrypted = TestPackageBuilder.Encrypt(plain, "121", IconKey);
        return new IconPackage("Icon", L2File.FromBytes(encrypted));
    }
}
