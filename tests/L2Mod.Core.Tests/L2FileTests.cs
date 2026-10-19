using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Tests;

public class L2FileTests
{
    [Theory]
    [InlineData("Icon.utx", 0x38)]
    [InlineData("Icon_e.utx", 0xFC)]
    public void DeriveVer121Key_MatchesRealClientFiles(string fileName, int expectedKey)
    {
        byte key = L2File.DeriveVer121Key(fileName);

        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void FromBytes_DecryptsVer111()
    {
        byte[] plain = BuildPackage();
        byte[] encrypted = TestPackageBuilder.Encrypt(plain, "111", 0xAC);

        L2File file = L2File.FromBytes(encrypted);

        Assert.Equal(L2Encryption.Ver111, file.Encryption);
        Assert.Equal(plain, file.Body);
    }

    [Fact]
    public void FromBytes_AcceptsPlainPackage()
    {
        byte[] plain = BuildPackage();

        L2File file = L2File.FromBytes(plain);

        Assert.Equal(L2Encryption.None, file.Encryption);
        Assert.Equal(0, file.ToFileOffset(0));
    }

    [Fact]
    public void FromBytes_RejectsActiveAnticheatFiles()
    {
        byte[] data = Encoding.ASCII.GetBytes("ActiveAnticheatCrypt" + new string('x', 64));

        Assert.Throws<NotSupportedException>(() => L2File.FromBytes(data));
    }

    [Fact]
    public void FromBytes_RejectsRsaVersions()
    {
        byte[] data = TestPackageBuilder.Encrypt(BuildPackage(), "413", 0);

        Assert.Throws<NotSupportedException>(() => L2File.FromBytes(data));
    }

    [Fact]
    public void BuildPatchedFile_OnlyTouchesPatchedRange()
    {
        byte[] plain = BuildPackage();
        L2File file = L2File.FromBytes(TestPackageBuilder.Encrypt(plain, "121", 0x38));

        byte[] patched = file.BuildPatchedFile(new[] { new BodyPatch(100, new byte[] { 1, 2, 3 }) });

        L2File reopened = L2File.FromBytes(patched);
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.Body.AsSpan(100, 3).ToArray());
        for (int i = 0; i < plain.Length; i++)
        {
            if (i < 100 || i >= 103)
            {
                Assert.Equal(plain[i], reopened.Body[i]);
            }
        }
    }

    private static byte[] BuildPackage()
    {
        RgbaImage image = TestPackageBuilder.SolidImage(4, 4, 10, 20, 30);
        return TestPackageBuilder.BuildPlain(image, 1);
    }
}
