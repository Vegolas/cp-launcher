using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

/// <summary>A synthetic Icon.utx (Ver121, key 0x38 like the real one) with a few 32x32 skill icons.</summary>
public static class TestIcons
{
    public const byte IconKey = 0x38;

    public static byte[] BuildIconUtx()
    {
        byte[] plain = TestPackageBuilder.BuildPlain(
            new TestTexture("skill0102", TestPackageBuilder.NoiseImage(32, 32, 102), 1),
            new TestTexture("skill0365", TestPackageBuilder.NoiseImage(32, 32, 365), 1),
            new TestTexture("skill1303", TestPackageBuilder.SolidImage(32, 32, 220, 30, 30), 1),
            new TestTexture("skill1059", TestPackageBuilder.SolidImage(32, 32, 120, 120, 200), 1));
        return TestPackageBuilder.Encrypt(plain, "121", IconKey);
    }

    public static IconPackage Open(byte[] fileBytes)
    {
        return new IconPackage("Icon", L2File.FromBytes(fileBytes));
    }

    public static IconPackage OpenDefault()
    {
        return Open(BuildIconUtx());
    }

    /// <summary>Raw DXT1 bytes of mip 0.</summary>
    public static byte[] Mip0(IconPackage package, string textureName)
    {
        MipLevel mip = package.FindTexture(textureName).Mips[0];
        return package.Package.Data.AsSpan(mip.DataOffset, mip.DataSize).ToArray();
    }

    public static byte[] Pixel(RgbaImage image, int x, int y)
    {
        return image.Pixels.AsSpan(image.GetOffset(x, y), 4).ToArray();
    }
}
