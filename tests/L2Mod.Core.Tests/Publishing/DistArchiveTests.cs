using System.IO.Compression;
using System.Text;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests.Publishing;

public sealed class DistArchiveTests : IDisposable
{
    private static readonly SigningKeyPair Keys = ManifestSignature.GenerateKeyPair();
    private static readonly string AssetName = "assets/" + new string('a', 64) + ".png";

    private readonly RecipeFixture _fixture = new RecipeFixture();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public void CreateThenExtract_GivesBackExactlyTheVerifiedFiles()
    {
        SignedDist.Write(_fixture, _fixture.DistPath, Keys);
        _fixture.Temp.WriteText(Path.Combine("dist", "launcher", "stray.exe"), "not referenced");
        DistVerification verification = DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem);
        string zip = _fixture.Temp.Combine("out", "dist.zip");

        IReadOnlyList<string> entries = DistArchive.Create(_fixture.DistPath, verification, zip);
        IReadOnlyList<string> extracted = DistArchive.Extract(zip, _fixture.Temp.Combine("extracted"));

        Assert.Equal(verification.ReferencedFiles.Concat(new[] { DistLayout.SignatureFile, DistLayout.ManifestFile }), entries);
        Assert.Equal(entries.OrderBy(entry => entry, StringComparer.Ordinal), extracted);
        Assert.True(DistVerifier.Verify(_fixture.Temp.Combine("extracted"), Keys.PublicKeyPem).IsValid);
        Assert.False(File.Exists(_fixture.Temp.Combine("extracted", "launcher", "stray.exe")));
    }

    [Fact]
    public void Create_NeedsAVerifiedDist()
    {
        var failed = new DistVerification(null, Array.Empty<string>(), new[] { "broken" });

        Assert.Throws<InvalidOperationException>(() => DistArchive.Create(_fixture.DistPath, failed, _fixture.Temp.Combine("dist.zip")));
    }

    [Theory]
    [InlineData("Icon.utx")]
    [InlineData("previews/badges.png")]
    [InlineData("../manifest.json")]
    [InlineData("/manifest.json")]
    [InlineData("launcher/..")]
    [InlineData("launcher/.")]
    [InlineData("assets\\aaaa.png")]
    [InlineData("launcher/sub/CpLauncher.exe")]
    public void Extract_RefusesWhatAPublishWouldNotWrite(string name)
    {
        string zip = WriteZip((name, "x"));

        Assert.Throws<InvalidDataException>(() => DistArchive.Extract(zip, _fixture.Temp.Combine("extracted")));

        Assert.False(File.Exists(_fixture.Temp.Combine("Icon.utx")));
        Assert.False(File.Exists(_fixture.Temp.Combine("manifest.json")));
    }

    [Fact]
    public void Extract_RefusesABackslashEvenInAValidLookingPath()
    {
        string zip = WriteZip((AssetName.Replace('/', '\\'), "x"));

        Assert.Throws<InvalidDataException>(() => DistArchive.Extract(zip, _fixture.Temp.Combine("extracted")));
    }

    [Fact]
    public void Extract_RefusesDuplicates()
    {
        string zip = WriteZip(("launcher/CpLauncher.exe", "a"), ("launcher/cplauncher.exe", "b"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => DistArchive.Extract(zip, _fixture.Temp.Combine("extracted")));

        Assert.Contains("twice", exception.Message);
    }

    [Fact]
    public void Extract_StopsAtTheSizeLimit()
    {
        string zip = WriteZip((DistLayout.ManifestFile, new string('x', 100)), (DistLayout.SignatureFile, "sig"));

        Assert.Throws<InvalidDataException>(() => DistArchive.Extract(zip, _fixture.Temp.Combine("extracted"), 50));
    }

    [Fact]
    public void Extract_SkipsFolderEntries()
    {
        string zip = WriteZip(("assets/", string.Empty), (AssetName, "png"), (DistLayout.ManifestFile, "{}"));

        IReadOnlyList<string> extracted = DistArchive.Extract(zip, _fixture.Temp.Combine("extracted"));

        Assert.Equal(new[] { AssetName, DistLayout.ManifestFile }, extracted);
    }

    [Fact]
    public void Extract_RefusesAFolderThatIsNotEmpty()
    {
        string zip = WriteZip((DistLayout.ManifestFile, "{}"));
        _fixture.Temp.WriteText(Path.Combine("extracted", "keep.txt"), "mine");

        Assert.Throws<IOException>(() => DistArchive.Extract(zip, _fixture.Temp.Combine("extracted")));

        Assert.Equal("mine", File.ReadAllText(_fixture.Temp.Combine("extracted", "keep.txt")));
    }

    private string WriteZip(params (string Name, string Content)[] entries)
    {
        string path = _fixture.Temp.Combine("crafted.zip");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach ((string name, string content) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name);
                    using (Stream output = entry.Open())
                    {
                        output.Write(Encoding.ASCII.GetBytes(content));
                    }
                }
            }
        }

        return path;
    }
}
