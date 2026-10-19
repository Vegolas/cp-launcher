using L2Mod.Cli.Leader;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests.Publishing;

public sealed class LeaderCommandsTests : IDisposable
{
    private readonly RecipeFixture _fixture = new RecipeFixture();
    private readonly StringWriter _output = new StringWriter();
    private readonly StringWriter _error = new StringWriter();
    private readonly LeaderDefaults _defaults;
    private readonly LeaderCommands _commands;

    public LeaderCommandsTests()
    {
        _defaults = new LeaderDefaults(
            _fixture.Temp.Combine("profile", ".l2mod", "signing-private.pem"),
            _fixture.Temp.Combine("repo", "keys", "signing-public.pem"),
            _fixture.PreviewPath);
        _commands = new LeaderCommands(_output, _error, _defaults);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public void Keygen_WritesBothKeysToTheDefaultLocations()
    {
        Assert.Equal(LeaderCommands.ExitOk, _commands.Run("keygen", Array.Empty<string>()));

        string privatePem = File.ReadAllText(_defaults.PrivateKeyPath);
        string publicPem = File.ReadAllText(_defaults.PublicKeyPath);
        Assert.Contains("BEGIN PRIVATE KEY", privatePem);
        Assert.Contains("BEGIN PUBLIC KEY", publicPem);
        Assert.Equal(publicPem, SigningKeys.GetPublicKeyPem(privatePem));
        Assert.Contains($"Fingerprint: {SigningKeys.GetFingerprint(publicPem)}", _output.ToString());
        Assert.Equal(SigningKeys.GetFingerprint(privatePem), SigningKeys.GetFingerprint(publicPem));
    }

    [Fact]
    public void Keygen_TakesExplicitPaths()
    {
        string privatePath = _fixture.Temp.Combine("elsewhere", "private.pem");
        string publicPath = _fixture.Temp.Combine("elsewhere", "public.pem");

        Assert.Equal(LeaderCommands.ExitOk, _commands.Keygen(new[] { "--private", privatePath, "--public", publicPath }));

        Assert.True(File.Exists(privatePath));
        Assert.True(File.Exists(publicPath));
        Assert.False(File.Exists(_defaults.PrivateKeyPath));
    }

    [Fact]
    public void Keygen_NeverOverwritesAPrivateKey()
    {
        string existing = _fixture.Temp.WriteText(Path.Combine("profile", ".l2mod", "signing-private.pem"), "my real key");

        IOException exception = Assert.Throws<IOException>(() => _commands.Keygen(Array.Empty<string>()));

        Assert.Contains("never overwrites", exception.Message);
        Assert.Equal("my real key", File.ReadAllText(existing));
        Assert.False(File.Exists(_defaults.PublicKeyPath));
    }

    [Fact]
    public void Keygen_RefusesToReplaceThePublicKeyTheLaunchersTrust()
    {
        _fixture.Temp.WriteText(Path.Combine("repo", "keys", "signing-public.pem"), "trusted");

        Assert.Throws<IOException>(() => _commands.Keygen(Array.Empty<string>()));

        Assert.False(File.Exists(_defaults.PrivateKeyPath));
        Assert.Equal("trusted", File.ReadAllText(_defaults.PublicKeyPath));
    }

    [Fact]
    public void Keygen_RefusesAPrivateKeyInsideAGitWorkTree()
    {
        Directory.CreateDirectory(_fixture.Temp.Combine("repo", ".git"));
        string privatePath = _fixture.Temp.Combine("repo", "keys", "signing-private.pem");

        IOException exception = Assert.Throws<IOException>(() => _commands.Keygen(new[] { "--private", privatePath }));

        Assert.Contains("git working tree", exception.Message);
        Assert.False(File.Exists(privatePath));
        Assert.Equal(_fixture.Temp.Combine("repo"), LeaderCommands.FindGitWorkTree(privatePath));
    }

    [Fact]
    public void BuildSignVerifyPublish_WorkTogether()
    {
        GenerateKeys();

        Assert.Equal(LeaderCommands.ExitOk, BuildDist());
        Assert.Equal(LeaderCommands.ExitOk, _commands.Run("sign", new[] { _fixture.DistPath }));
        Assert.Equal(LeaderCommands.ExitOk, _commands.Run("verify", new[] { _fixture.DistPath }));
        Assert.Equal(LeaderCommands.ExitOk, _commands.Run("publish", new[] { _fixture.DistPath, "--bucket", "cp-recipes" }));

        Assert.Empty(_error.ToString());
        Assert.True(File.Exists(Path.Combine(_fixture.PreviewPath, "badges.png")));
        Assert.Contains("1. skill0102 CC #E02020  # Stun", File.ReadAllText(Path.Combine(_fixture.PreviewPath, "tags.txt")));
        string output = _output.ToString();
        Assert.Contains("DRY RUN", output);
        string[] plan = output.Split('\n').Where(line => line.Contains("Cache-Control")).ToArray();
        Assert.Equal(3, plan.Length);
        Assert.Contains("assets/", plan[0]);
        Assert.Contains(UploadPlan.ImmutableCache, plan[0]);
        Assert.Contains("manifest.json.sig", plan[1]);
        Assert.Contains("manifest.json  [application/json", plan[2]);
        Assert.Contains("wrangler r2 object put \"cp-recipes/manifest.json\"", output);
    }

    [Fact]
    public void Verify_FailsAfterAOneByteChange()
    {
        GenerateKeys();
        BuildDist();
        _commands.Sign(new[] { _fixture.DistPath });
        string manifestPath = Path.Combine(_fixture.DistPath, DistLayout.ManifestFile);
        byte[] manifest = File.ReadAllBytes(manifestPath);
        manifest[10] ^= 0x20;
        File.WriteAllBytes(manifestPath, manifest);

        Assert.Equal(LeaderCommands.ExitFailed, _commands.Verify(new[] { _fixture.DistPath }));
        Assert.Equal(LeaderCommands.ExitFailed, _commands.Publish(new[] { _fixture.DistPath }));

        Assert.Contains("signature is not valid", _error.ToString());
        Assert.Contains("Verification FAILED.", _error.ToString());
        Assert.Contains("Nothing published.", _error.ToString());
    }

    [Fact]
    public void Sign_RefusesAKeyThatDoesNotMatchThePublicKey()
    {
        GenerateKeys();
        BuildDist();
        string otherKey = _fixture.Temp.WriteText("other-private.pem", ManifestSignature.GenerateKeyPair().PrivateKeyPem);

        Assert.Equal(LeaderCommands.ExitFailed, _commands.Sign(new[] { _fixture.DistPath, "--key", otherKey }));

        Assert.Contains("does not belong to the public key", _error.ToString());
        Assert.False(File.Exists(Path.Combine(_fixture.DistPath, DistLayout.SignatureFile)));
    }

    [Fact]
    public void Sign_WarnsWhenThereIsNoPublicKeyToCheckAgainst()
    {
        GenerateKeys();
        File.Delete(_defaults.PublicKeyPath);
        BuildDist();

        Assert.Equal(LeaderCommands.ExitOk, _commands.Sign(new[] { _fixture.DistPath }));

        Assert.Contains("warning: no public key", _output.ToString());
    }

    [Fact]
    public void Sign_RefusesADistTheLauncherWouldNotReadInFull()
    {
        GenerateKeys();
        BuildDist();
        File.Delete(DistLayout.GetFullPath(_fixture.DistPath, DistWriter.ListFiles(_fixture.DistPath)[0]));

        Assert.Equal(LeaderCommands.ExitFailed, _commands.Sign(new[] { _fixture.DistPath }));

        Assert.Contains("is missing", _error.ToString());
        Assert.Contains("Not signed.", _error.ToString());
    }

    [Fact]
    public void Build_KeepsPreviewsOutOfTheUploadFolder()
    {
        string previews = Path.Combine(_fixture.DistPath, "previews");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => BuildDist("--previews", previews));

        Assert.Contains("NCSoft", exception.Message);
        Assert.False(Directory.Exists(_fixture.DistPath));
    }

    [Fact]
    public void Build_RefusesToWriteNextToTheOriginal()
    {
        string clientFolder = Path.GetDirectoryName(_fixture.OriginalPath)!;

        Assert.Throws<ArgumentException>(() => _commands.Build(new[] { _fixture.RecipesPath, _fixture.OriginalPath, clientFolder }));

        Assert.Equal(new[] { "Icon.utx" }, DistWriter.ListFiles(clientFolder));
    }

    [Fact]
    public void Build_NeedsOverwriteForAnEarlierBuild()
    {
        BuildDist();

        Assert.Throws<IOException>(() => BuildDist());
        Assert.Equal(LeaderCommands.ExitOk, BuildDist("--overwrite", "--version", "2026.10.21.1"));

        Assert.Contains("Version 2026.10.21.1", _output.ToString());
    }

    [Fact]
    public void Commands_PrintUsageForWrongArguments()
    {
        Assert.Equal(LeaderCommands.ExitUsage, _commands.Build(new[] { _fixture.RecipesPath }));
        Assert.Equal(LeaderCommands.ExitUsage, _commands.Keygen(new[] { "extra" }));
        Assert.Equal(LeaderCommands.ExitUsage, _commands.Verify(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => _commands.Sign(new[] { _fixture.DistPath, "--unknown", "x" }));

        Assert.Contains("keygen  [--private <path>]", _error.ToString());
    }

    [Fact]
    public void Verify_NeedsAPublicKey()
    {
        BuildDist();

        Assert.Throws<FileNotFoundException>(() => _commands.Verify(new[] { _fixture.DistPath }));
    }

    private void GenerateKeys()
    {
        Assert.Equal(LeaderCommands.ExitOk, _commands.Keygen(Array.Empty<string>()));
    }

    private int BuildDist(params string[] options)
    {
        return _commands.Build(new[] { _fixture.RecipesPath, _fixture.OriginalPath, _fixture.DistPath }.Concat(options).ToArray());
    }
}
