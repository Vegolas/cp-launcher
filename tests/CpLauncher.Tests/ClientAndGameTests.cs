using CpLauncher.Services;
using L2Mod.Core.Signing;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

public class ClientAndGameTests
{
    [Fact]
    public void ClientFolder_AcceptsAFolderWithIconUtxAndL2Exe()
    {
        var fixture = new LauncherFixture();

        ClientFolderCheck check = ClientFolder.Validate(fixture.FileSystem, @"C:\client\");

        Assert.True(check.IsValid);
        Assert.Equal(@"C:\client", check.Directory);
    }

    [Fact]
    public void ClientFolder_ReportsEachMissingFile()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.Seed(@"C:\other\SysTextures\Icon.utx", new byte[] { 1 });

        ClientFolderCheck check = ClientFolder.Validate(fileSystem, @"C:\other");

        string error = Assert.Single(check.Errors);
        Assert.Contains(@"system_season\L2.exe", error);
        Assert.Equal(2, ClientFolder.Validate(fileSystem, @"C:\empty").Errors.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"Lineage2Dex")]
    [InlineData(@"\Lineage2Dex")]
    public void ClientFolder_RejectsEmptyAndRelativePaths(string? directory)
    {
        ClientFolderCheck check = ClientFolder.Validate(new InMemoryFileSystem(), directory);

        Assert.False(check.IsValid);
        Assert.Single(check.Errors);
    }

    [Fact]
    public void ClientFolder_AcceptsAQuotedPath()
    {
        var fixture = new LauncherFixture();

        Assert.True(ClientFolder.Validate(fixture.FileSystem, "\"C:\\client\"").IsValid);
    }

    [Theory]
    [InlineData("L2Updater.exe", @"C:\client\L2Updater.exe")]
    [InlineData(@"data\L2Launcher.exe", @"C:\client\data\L2Launcher.exe")]
    [InlineData("data/L2Launcher.exe", @"C:\client\data\L2Launcher.exe")]
    public void GameExecutables_ResolvesInsideTheClientFolder(string relative, string expected)
    {
        Assert.Equal(expected, GameExecutables.Resolve(@"C:\client", relative));
    }

    [Theory]
    [InlineData(@"..\evil.exe")]
    [InlineData(@"data\..\..\evil.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"system_season\L2.ini")]
    [InlineData("")]
    public void GameExecutables_RejectsPathsOutsideTheFolderAndNonExecutables(string relative)
    {
        Assert.Null(GameExecutables.Resolve(@"C:\client", relative));
    }

    [Fact]
    public void GameExecutables_OffersBothDexCandidates()
    {
        Assert.Equal(new[] { "L2Updater.exe", @"data\L2Launcher.exe" }, GameExecutables.Options.Select(option => option.RelativePath));
        Assert.Equal("L2Updater.exe", GameExecutables.Default);
    }

    [Theory]
    [InlineData("1.0.1", true)]
    [InlineData("1.1.0", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.0.0", false)]
    [InlineData("0.9.9", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void LauncherVersion_DetectsNewerReleases(string? candidate, bool newer)
    {
        Assert.Equal(newer, LauncherVersion.IsNewer(candidate, new Version(1, 0, 0)));
    }

    [Fact]
    public void LauncherVersion_IsTheProjectVersion()
    {
        Assert.Equal(new Version(1, 0, 0), LauncherVersion.Current);
    }

    [Fact]
    public void EmbeddedKey_IsAValidP256PublicKeyThatRejectsForeignSignatures()
    {
        TrustedKey key = TrustedKeys.Embedded;
        SigningKeyPair other = ManifestSignature.GenerateKeyPair();
        byte[] data = new byte[] { 1, 2, 3 };

        Assert.False(key.IsDevelopment);
        Assert.True(TrustedKeys.IsValidPublicKey(key.PublicKeyPem));
        Assert.False(ManifestSignature.Verify(data, ManifestSignature.Sign(data, other.PrivateKeyPem), key.PublicKeyPem));
        Assert.DoesNotContain("PRIVATE KEY-----", key.PublicKeyPem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevelopmentKey_MustBeAPublicKey()
    {
        SigningKeyPair pair = ManifestSignature.GenerateKeyPair();

        Assert.True(TrustedKeys.Development(pair.PublicKeyPem, "dev.pem").IsDevelopment);
        Assert.Throws<FormatException>(() =>
        {
            TrustedKeys.Development("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", "bad.pem");
        });
    }

    [Theory]
    [InlineData(1, "ikona")]
    [InlineData(2, "ikony")]
    [InlineData(4, "ikony")]
    [InlineData(5, "ikon")]
    [InlineData(12, "ikon")]
    [InlineData(22, "ikony")]
    [InlineData(25, "ikon")]
    [InlineData(0, "ikon")]
    [InlineData(134, "ikony")]
    public void Polish_PluralForms(int count, string expected)
    {
        Assert.Equal(expected, Polish.Plural(count, "ikona", "ikony", "ikon"));
    }
}
