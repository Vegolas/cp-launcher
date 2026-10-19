using System.Text.Json.Nodes;
using CpLauncher.Services;
using L2Mod.Core.Signing;

namespace CpLauncher.Tests;

public class ManifestServiceTests
{
    private readonly LauncherFixture _fixture = new LauncherFixture();
    private readonly ManifestService _service;
    private readonly FolderManifestSource _source;

    public ManifestServiceTests()
    {
        _service = new ManifestService(_fixture.FileSystem, _fixture.Paths, _fixture.TrustedKeys());
        _source = new FolderManifestSource(_fixture.FileSystem, LauncherFixture.SourceDirectory);
    }

    [Fact]
    public async Task Load_VerifiesAndCachesTheManifest()
    {
        _fixture.Publish(_fixture.Manifest());

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Manifest);
        Assert.Equal(ManifestOrigin.Source, result.Manifest.Origin);
        Assert.Equal("2026.10.20.1", result.Manifest.Manifest.Version);
        Assert.Equal(new[] { "badges", "tags", "marks" }, result.Manifest.Manifest.Packs.Select(pack => pack.Id));
        Assert.True(_fixture.FileSystem.FileExists(_fixture.Paths.CachedManifestPath));
        Assert.True(_fixture.FileSystem.FileExists(_fixture.Paths.CachedSignaturePath));
    }

    [Fact]
    public async Task Load_Offline_UsesTheLastVerifiedManifest()
    {
        _fixture.Publish(_fixture.Manifest());
        await _service.LoadAsync(_source, CancellationToken.None);
        _fixture.TakeSourceOffline();

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.NotNull(result.Manifest);
        Assert.Equal(ManifestOrigin.Cache, result.Manifest.Origin);
        Assert.Contains("Brak dostępu", result.Problem);
    }

    [Fact]
    public async Task Load_Offline_WithoutCache_HasNoManifest()
    {
        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Contains("Brak zapisanej kopii", result.Problem);
    }

    [Fact]
    public async Task Load_WithoutSource_UsesTheCache()
    {
        _fixture.Publish(_fixture.Manifest());
        await _service.LoadAsync(_source, CancellationToken.None);

        ManifestLoadResult result = await _service.LoadAsync(null, CancellationToken.None);

        Assert.Equal(ManifestOrigin.Cache, result.Manifest?.Origin);
        Assert.Contains("Nie ustawiono źródła", result.Problem);
    }

    [Fact]
    public async Task Load_ChangedByte_IsRejectedAndDoesNotReplaceTheCache()
    {
        _fixture.Publish(_fixture.Manifest());
        await _service.LoadAsync(_source, CancellationToken.None);
        JsonObject tampered = _fixture.Manifest();
        tampered["version"] = "2026.10.21.1";
        byte[] bytes = TestManifests.Serialize(tampered);
        byte[] signature = ManifestSignature.Sign(bytes, _fixture.Key.PrivateKeyPem);
        bytes[bytes.Length - 2] ^= 0x01;
        _fixture.PublishBytes(bytes, signature);

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.Contains("nieprawidłowy podpis", result.Problem);
        Assert.Equal(ManifestOrigin.Cache, result.Manifest?.Origin);
        Assert.Equal("2026.10.20.1", result.Manifest?.Manifest.Version);
    }

    [Fact]
    public async Task Load_ManifestSignedWithAnotherKey_IsRejected()
    {
        _fixture.Publish(_fixture.Manifest(), ManifestSignature.GenerateKeyPair().PrivateKeyPem);

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Contains("nieprawidłowy podpis", result.Problem);
        Assert.False(_fixture.FileSystem.FileExists(_fixture.Paths.CachedManifestPath));
    }

    [Fact]
    public async Task Load_SignedButMalformedManifest_IsRejected()
    {
        byte[] bytes = "{ \"schema\": 1 }"u8.ToArray();
        _fixture.PublishBytes(bytes, ManifestSignature.Sign(bytes, _fixture.Key.PrivateKeyPem));

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Contains("nie da się go odczytać", result.Problem);
    }

    [Fact]
    public async Task LoadCached_RejectsATamperedCache()
    {
        _fixture.Publish(_fixture.Manifest());
        await _service.LoadAsync(_source, CancellationToken.None);
        byte[] cached = _fixture.FileSystem.ReadAllBytes(_fixture.Paths.CachedManifestPath);
        cached[10] ^= 0x01;
        _fixture.FileSystem.Seed(_fixture.Paths.CachedManifestPath, cached);

        Assert.Null(_service.LoadCached());
    }

    [Fact]
    public async Task Load_ReportsWhichKeyVerifiedTheManifest()
    {
        _fixture.Publish(_fixture.Manifest());
        SigningKeyPair production = ManifestSignature.GenerateKeyPair();
        var keys = new[]
        {
            new TrustedKey("CP", production.PublicKeyPem, false),
            new TrustedKey("dev", _fixture.Key.PublicKeyPem, true),
        };
        var service = new ManifestService(_fixture.FileSystem, _fixture.Paths, keys);

        ManifestLoadResult result = await service.LoadAsync(_source, CancellationToken.None);

        Assert.True(result.Manifest?.Key.IsDevelopment);
    }

    [Fact]
    public async Task Load_NewerSchema_KeepsOnlyTheEnvelope()
    {
        var manifest = new JsonObject
        {
            ["schema"] = 2,
            ["enabled"] = false,
            ["launcher"] = new JsonObject { ["version"] = "2.0.0", ["url"] = "launcher/CpLauncher-2.0.0.exe", ["sha256"] = new string('c', 64) },
        };
        _fixture.Publish(manifest);

        ManifestLoadResult result = await _service.LoadAsync(_source, CancellationToken.None);

        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Manifest.Manifest.Schema);
        Assert.False(result.Manifest.Manifest.Enabled);
        Assert.Empty(result.Manifest.Manifest.Packs);
        Assert.Single(result.Manifest.Messages);
    }
}
