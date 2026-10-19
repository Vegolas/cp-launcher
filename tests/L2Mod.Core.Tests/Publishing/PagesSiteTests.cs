using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests.Publishing;

public sealed class PagesSiteTests : IDisposable
{
    private static readonly SigningKeyPair Keys = ManifestSignature.GenerateKeyPair();

    private readonly RecipeFixture _fixture = new RecipeFixture();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private string Previous
    {
        get
        {
            return _fixture.Temp.Combine("previous");
        }
    }

    private string Site
    {
        get
        {
            return _fixture.Temp.Combine("site");
        }
    }

    [Fact]
    public void Stage_WritesTheVerifiedDist()
    {
        SignedDist.Write(_fixture, _fixture.DistPath, Keys);

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, null, Site);

        Assert.True(result.IsValid);
        Assert.Equal(RecipeFixture.Version, result.Manifest!.Version);
        Assert.Equal(DistWriter.ListFiles(_fixture.DistPath), DistWriter.ListFiles(Site));
        Assert.Empty(result.KeptFromPrevious);
        Assert.True(DistVerifier.Verify(Site, Keys.PublicKeyPem).IsValid);
    }

    [Fact]
    public void Stage_RefusesADistSignedForAnotherKey()
    {
        SignedDist.Write(_fixture, _fixture.DistPath, ManifestSignature.GenerateKeyPair());

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, null, Site);

        Assert.False(result.IsValid);
        Assert.Contains("signature is not valid", string.Join(" ", result.Errors));
        Assert.False(Directory.Exists(Site));
    }

    [Fact]
    public void Stage_RefusesAFileTheManifestDoesNotReference()
    {
        SignedDist.Write(_fixture, _fixture.DistPath, Keys);
        _fixture.Temp.WriteText(Path.Combine("dist", "launcher", "extra.exe"), "smuggled");

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, null, Site);

        Assert.Contains("launcher/extra.exe is not referenced by the manifest; a published dist holds nothing else.", result.Errors);
        Assert.False(Directory.Exists(Site));
    }

    [Fact]
    public void Stage_KeepsTheFilesOfThePreviousVersion()
    {
        SignedDist.AddLauncher(_fixture, "1.0.0", "MZ launcher 1.0.0");
        DistBuildResult before = SignedDist.Write(_fixture, Previous, Keys, "1");
        SignedDist.ChangeOverlay(_fixture);
        SignedDist.AddLauncher(_fixture, "1.0.1", "MZ launcher 1.0.1");
        DistBuildResult now = SignedDist.Write(_fixture, _fixture.DistPath, Keys, "2");

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, Previous, Site);

        Assert.True(result.IsValid);
        string oldAsset = before.Files.Single(file => file.Path.StartsWith("assets/", StringComparison.Ordinal)).Path;
        Assert.Equal(new[] { oldAsset, "launcher/CpLauncher-1.0.0.exe" }, result.KeptFromPrevious);
        Assert.Equal(before.Files.Concat(now.Files).Select(file => file.Path).Order(StringComparer.Ordinal), DistWriter.ListFiles(Site).Where(file => !file.StartsWith("manifest", StringComparison.Ordinal)));
        Assert.Equal(now.ManifestBytes, File.ReadAllBytes(Path.Combine(Site, DistLayout.ManifestFile)));
        Assert.True(DistVerifier.Verify(Site, Keys.PublicKeyPem).IsValid);
    }

    [Fact]
    public void Stage_RefusesANameThePreviousVersionUsedForOtherBytes()
    {
        SignedDist.AddLauncher(_fixture, "1.0.0", "MZ first build");
        SignedDist.Write(_fixture, Previous, Keys, "1");
        SignedDist.AddLauncher(_fixture, "1.0.0", "MZ rebuilt, same version");
        SignedDist.Write(_fixture, _fixture.DistPath, Keys, "2");

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, Previous, Site);

        Assert.False(result.IsValid);
        Assert.StartsWith("launcher/CpLauncher-1.0.0.exe has different bytes", Assert.Single(result.Errors));
        Assert.False(Directory.Exists(Site));
    }

    [Fact]
    public void Stage_SkipsABrokenPreviousVersionWithAWarning()
    {
        DistBuildResult before = SignedDist.Write(_fixture, Previous, Keys, "1");
        File.Delete(DistLayout.GetFullPath(Previous, before.Files[0].Path));
        SignedDist.ChangeOverlay(_fixture);
        SignedDist.Write(_fixture, _fixture.DistPath, Keys, "2");

        PagesStageResult result = PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, Previous, Site);

        Assert.True(result.IsValid);
        Assert.Empty(result.KeptFromPrevious);
        Assert.Contains("is missing", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Stage_RefusesASiteFolderThatIsNotEmpty()
    {
        SignedDist.Write(_fixture, _fixture.DistPath, Keys);
        _fixture.Temp.WriteText(Path.Combine("site", "index.html"), "mine");

        Assert.Throws<IOException>(() => PagesSite.Stage(_fixture.DistPath, Keys.PublicKeyPem, null, Site));

        Assert.Equal(new[] { "index.html" }, DistWriter.ListFiles(Site));
    }
}
