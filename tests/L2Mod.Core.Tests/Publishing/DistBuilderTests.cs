using System.Text;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests.Publishing;

public sealed class DistBuilderTests : IDisposable
{
    private static readonly SigningKeyPair Keys = ManifestSignature.GenerateKeyPair();

    private readonly RecipeFixture _fixture = new RecipeFixture();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public void Build_ProducesAManifestTheLauncherReadsInFull()
    {
        DistBuildResult build = Build();

        ManifestReadResult result = ManifestParser.Parse(build.ManifestBytes);
        Assert.Empty(result.Messages);
        Manifest manifest = result.Manifest;
        Assert.Equal((1, RecipeFixture.Version, true), (manifest.Schema, manifest.Version, manifest.Enabled));
        Assert.Equal(new[] { ContentHash.Sha256Hex(_fixture.OriginalBytes) }, manifest.SupportedOriginals["Icon.utx"]);
        Assert.Equal(ContentHash.Sha256Hex(_fixture.OriginalBytes), build.OriginalSha256);
        Assert.Equal(RecipeFixture.PackIds, manifest.Packs.Select(pack => pack.Id));
        Assert.Equal(new[] { true, true, false }, manifest.Packs.Select(pack => pack.Default));
        Assert.IsType<BadgeOperation>(manifest.Packs[0].Operations.Single());
        Assert.IsType<TagOperation>(manifest.Packs[1].Operations.Single());
        OverlayOperation overlay = Assert.IsType<OverlayOperation>(manifest.Packs[2].Operations.Single());
        Assert.Equal("Start", manifest.Announcements.Single().Title);
        Assert.Equal("mass-pvp", manifest.OptionPresets.Single().Id);
        DistFile asset = Assert.Single(build.Files);
        Assert.Equal((overlay.Image, overlay.ImageSha256), (asset.Path, ContentHash.Sha256Hex(asset.Bytes)));
        Assert.Contains("\"description\": \"Linia 1\\nLinia 2\"", Encoding.UTF8.GetString(build.ManifestBytes));
        Assert.Empty(build.Warnings);
    }

    [Fact]
    public void Build_IsDeterministicAndTakesTheVersionOverride()
    {
        DistBuildResult first = Build();
        DistBuildResult second = Build();
        DistBuildResult overridden = Build(new DistBuildOptions { Version = "2026.10.21.1" });

        Assert.Equal(first.ManifestBytes, second.ManifestBytes);
        Assert.Equal("2026.10.21.1", ManifestParser.Parse(overridden.ManifestBytes).Manifest.Version);
    }

    [Fact]
    public void Build_RendersOnePreviewPerPack()
    {
        DistBuildResult build = Build(new DistBuildOptions { PreviewScale = 2, PreviewColumns = 1 });

        Assert.Equal(RecipeFixture.PackIds, build.Previews.Select(preview => preview.PackId));
        PackPreview badges = build.Previews[0];
        Assert.Equal(new[] { "  1. skill0365 <- skill1303  # Dance -> Might" }, badges.Index);
        Assert.Equal(((32 * 2 * 2) + 2 + 12, (32 * 2) + 12), (badges.Sheet.Width, badges.Sheet.Height));
    }

    [Fact]
    public void Build_AppliesAllPacksToTheOriginalLikeTheLauncher()
    {
        DistBuildResult build = Build();
        IconPackage original = TestIcons.Open(_fixture.OriginalBytes);
        var installed = new InMemoryFileSystem();
        installed.Seed(Path.Combine(@"C:\client", "SysTextures", "Icon.utx"), _fixture.OriginalBytes);
        DistWriter.Write(_fixture.DistPath, build, false);
        File.WriteAllBytes(Path.Combine(_fixture.DistPath, DistLayout.SignatureFile), ManifestSignature.Sign(build.ManifestBytes, Keys.PrivateKeyPem));
        byte[] signature = File.ReadAllBytes(Path.Combine(_fixture.DistPath, DistLayout.SignatureFile));
        Manifest manifest = ManifestSignature.ReadVerified(File.ReadAllBytes(Path.Combine(_fixture.DistPath, DistLayout.ManifestFile)), signature, Keys.PublicKeyPem).Manifest;
        var installer = new PatchInstaller(new InstallerOptions(@"C:\client", @"C:\data"), installed, new FakeProcessMonitor());

        PatchFileResult result = Assert.Single(installer.Apply(manifest, RecipeFixture.PackIds, new DirectoryAssetStore(new PhysicalFileSystem(), _fixture.DistPath)));

        Assert.Equal(PatchOutcome.Installed, result.Outcome);
        byte[] expected = OperationComposer.Build(original, build.Packs.SelectMany(pack => pack.Operations).ToList(), new InMemoryAssets(build.Files)).Replacement.FileBytes;
        Assert.Equal(expected, installed.ReadAllBytes(Path.Combine(@"C:\client", "SysTextures", "Icon.utx")));
    }

    [Fact]
    public void Build_RequiresTheOriginalUnderItsClientName()
    {
        string renamed = _fixture.Temp.WriteBytes("Icon_c.utx", _fixture.OriginalBytes);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => DistBuilder.Build(RecipeProject.Load(_fixture.RecipesPath), renamed));

        Assert.Contains("Icon.utx", exception.Message);
    }

    [Fact]
    public void Build_FailsOnATextureMissingFromTheOriginal()
    {
        _fixture.Temp.WriteText(Path.Combine("recipes", "mappings", "badges.txt"), "skill9999  skill1303\n");

        Assert.Throws<KeyNotFoundException>(() => Build());
    }

    [Fact]
    public void Build_WarnsWhenPacksDrawIntoTheSameCorner()
    {
        _fixture.WriteRecipes(RecipeFixture.DefaultRecipesJson.Replace("\"corner\": \"tr\"", "\"corner\": \"tl\""));
        _fixture.Temp.WriteText(Path.Combine("recipes", "mappings", "marks.txt"), "skill0102  marks/dot.png\n");

        DistBuildResult build = Build();

        Assert.Contains(build.Warnings, warning => warning.Contains("both use corner tl"));
    }

    [Fact]
    public void Build_FailsWhenTheLauncherWouldSkipSomething()
    {
        _fixture.WriteRecipes(RecipeFixture.DefaultRecipesJson.Replace("\"id\": \"tags\"", "\"id\": \"Tags\""));

        ManifestException exception = Assert.Throws<ManifestException>(() => Build());

        Assert.Contains("Pack 'Tags' skipped", exception.Message);
    }

    [Fact]
    public void Build_PublishesTheLauncherUnderItsVersion()
    {
        byte[] exe = Encoding.ASCII.GetBytes("MZ fake launcher");
        _fixture.Temp.WriteBytes(Path.Combine("bin", "CpLauncher.exe"), exe);
        _fixture.WriteRecipes(RecipeFixture.DefaultRecipesJson.Replace("\"packs\":", "\"launcher\": { \"version\": \"1.0.0\", \"file\": \"../bin/CpLauncher.exe\" },\n\"packs\":"));

        DistBuildResult build = Build();

        Assert.Equal(new LauncherRelease("1.0.0", "launcher/CpLauncher-1.0.0.exe", ContentHash.Sha256Hex(exe)), build.Manifest.Launcher);
        Assert.Contains(build.Files, file => file.Path == "launcher/CpLauncher-1.0.0.exe" && file.Bytes.SequenceEqual(exe));
        Assert.Equal(build.Manifest.Launcher, ManifestParser.Parse(build.ManifestBytes).Manifest.Launcher);
    }

    [Fact]
    public void Write_CreatesTheLayoutWithTheManifestLast()
    {
        DistBuildResult build = Build();

        DistWriteResult written = DistWriter.Write(_fixture.DistPath, build, false);

        Assert.Equal(new[] { build.Files[0].Path, DistLayout.ManifestFile }, written.Written);
        Assert.Equal(build.ManifestBytes, File.ReadAllBytes(Path.Combine(_fixture.DistPath, DistLayout.ManifestFile)));
        Assert.Equal(new[] { build.Files[0].Path, DistLayout.ManifestFile }, DistWriter.ListFiles(_fixture.DistPath));
    }

    [Fact]
    public void Write_RefusesAFolderWithOtherFiles()
    {
        string foreign = _fixture.Temp.WriteText(Path.Combine("dist", "Icon.utx"), "not ours");

        IOException exception = Assert.Throws<IOException>(() => DistWriter.Write(_fixture.DistPath, Build(), true));

        Assert.Contains("not a build output folder", exception.Message);
        Assert.Equal("not ours", File.ReadAllText(foreign));
        Assert.Single(DistWriter.ListFiles(_fixture.DistPath));
    }

    [Fact]
    public void Write_ReplacesAnEarlierBuildOnlyWithOverwrite()
    {
        DistWriter.Write(_fixture.DistPath, Build(), false);
        string signature = _fixture.Temp.WriteText(Path.Combine("dist", DistLayout.SignatureFile), "old");
        string stale = _fixture.Temp.WriteText(Path.Combine("dist", "assets", new string('0', 64) + ".png"), "stale");

        Assert.Throws<IOException>(() => DistWriter.Write(_fixture.DistPath, Build(), false));
        DistWriteResult written = DistWriter.Write(_fixture.DistPath, Build(new DistBuildOptions { Version = "2" }), true);

        Assert.False(File.Exists(signature));
        Assert.False(File.Exists(stale));
        Assert.Contains(DistLayout.SignatureFile, written.Removed);
        Assert.Empty(written.Warnings);
    }

    [Fact]
    public void Write_WarnsWhenTheManifestChangedButNotItsVersion()
    {
        DistWriter.Write(_fixture.DistPath, Build(), false);
        _fixture.Temp.WriteText(Path.Combine("recipes", "mappings", "tags.txt"), "skill0102  DOT\n");

        DistWriteResult written = DistWriter.Write(_fixture.DistPath, Build(), true);

        Assert.Contains(written.Warnings, warning => warning.Contains("version is still"));
    }

    [Fact]
    public void Verify_AcceptsASignedBuild()
    {
        WriteSignedDist();

        DistVerification result = DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem);

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(RecipeFixture.Version, result.Manifest!.Version);
        Assert.Single(result.ReferencedFiles);
    }

    [Fact]
    public void Verify_RejectsAOneByteChangeOfTheManifest()
    {
        WriteSignedDist();
        string path = Path.Combine(_fixture.DistPath, DistLayout.ManifestFile);
        byte[] manifest = File.ReadAllBytes(path);
        manifest[manifest.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, manifest);

        DistVerification result = DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem);

        Assert.False(result.IsValid);
        Assert.Contains("signature", Assert.Single(result.Errors));
    }

    [Fact]
    public void Verify_RejectsAnotherKeyAMissingSignatureAndChangedAssets()
    {
        WriteSignedDist();
        Assert.False(DistVerifier.Verify(_fixture.DistPath, ManifestSignature.GenerateKeyPair().PublicKeyPem).IsValid);

        string asset = DistLayout.GetFullPath(_fixture.DistPath, DistWriter.ListFiles(_fixture.DistPath)[0]);
        byte[] png = File.ReadAllBytes(asset);
        png[png.Length - 13] ^= 0x01;
        File.WriteAllBytes(asset, png);
        Assert.Contains(DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem).Errors, error => error.Contains("does not match its SHA-256"));

        File.Delete(asset);
        Assert.Contains(DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem).Errors, error => error.Contains("is missing"));

        File.Delete(Path.Combine(_fixture.DistPath, DistLayout.SignatureFile));
        Assert.Contains("run sign first", Assert.Single(DistVerifier.Verify(_fixture.DistPath, Keys.PublicKeyPem).Errors));
    }

    [Fact]
    public void Verify_WithoutAKeyChecksContentOnly()
    {
        DistWriter.Write(_fixture.DistPath, Build(), false);

        Assert.True(DistVerifier.Verify(_fixture.DistPath, null).IsValid);
        Assert.False(DistVerifier.Verify(_fixture.Temp.Combine("nothing"), null).IsValid);
    }

    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("manifest.json.sig", true)]
    [InlineData("assets/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("launcher/CpLauncher-1.0.0.exe", true)]
    [InlineData("assets/x.png", false)]
    [InlineData("Icon.utx", false)]
    [InlineData("previews/badges.png", false)]
    [InlineData("launcher/sub/CpLauncher.exe", false)]
    public void IsBuildOutput_KnowsTheLayout(string path, bool expected)
    {
        Assert.Equal(expected, DistLayout.IsBuildOutput(path));
    }

    [Fact]
    public void GetFullPath_StaysInsideTheFolder()
    {
        Assert.Throws<ArgumentException>(() => DistLayout.GetFullPath(_fixture.DistPath, "../Icon.utx"));
        Assert.Equal(Path.Combine(_fixture.DistPath, "assets", "a.png"), DistLayout.GetFullPath(_fixture.DistPath, "assets/a.png"));
    }

    private DistBuildResult Build(DistBuildOptions? options = null)
    {
        return DistBuilder.Build(RecipeProject.Load(_fixture.RecipesPath), _fixture.OriginalPath, options);
    }

    private void WriteSignedDist()
    {
        DistBuildResult build = Build();
        DistWriter.Write(_fixture.DistPath, build, false);
        File.WriteAllBytes(Path.Combine(_fixture.DistPath, DistLayout.SignatureFile), ManifestSignature.Sign(build.ManifestBytes, Keys.PrivateKeyPem));
    }

    private sealed class InMemoryAssets : IAssetStore
    {
        private readonly IReadOnlyList<DistFile> _files;

        public InMemoryAssets(IReadOnlyList<DistFile> files)
        {
            _files = files;
        }

        public byte[] ReadAsset(string path)
        {
            return _files.Single(file => file.Path == path).Bytes;
        }
    }
}
