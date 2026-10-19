using System.Net;
using CpLauncher.Services;
using L2Mod.Core.Manifests;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

public class ManifestSourceTests
{
    private readonly StubHttpHandler _handler = new StubHttpHandler();
    private readonly InMemoryFileSystem _fileSystem = new InMemoryFileSystem();

    [Fact]
    public async Task Http_ReadsFilesRelativeToTheBaseAddressWithoutCachingTheManifest()
    {
        _handler.Add("/cp/manifest.json", new byte[] { 1, 2, 3 });
        _handler.Add("/cp/assets/" + new string('a', 64) + ".png", new byte[] { 4 });
        var source = new HttpManifestSource(new HttpClient(_handler), new Uri("https://cdn.example.org/cp"));

        byte[] manifest = await source.ReadAsync("manifest.json", 100, CancellationToken.None);
        byte[] asset = await source.ReadAsync("assets/" + new string('a', 64) + ".png", 100, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, manifest);
        Assert.Equal(new byte[] { 4 }, asset);
        Assert.Equal("https://cdn.example.org/cp/", source.Description);
        Assert.True(_handler.Requests[0].Headers.CacheControl?.NoCache);
        Assert.Null(_handler.Requests[1].Headers.CacheControl);
    }

    [Fact]
    public async Task Http_MapsConnectionFailuresAndMissingFilesToUnavailable()
    {
        _handler.Responses["/cp/manifest.json.sig"] = () =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        _handler.Responses["/cp/manifest.json"] = () =>
        {
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        };
        var source = new HttpManifestSource(new HttpClient(_handler), new Uri("https://cdn.example.org/cp/"));
        var offline = new HttpManifestSource(new HttpClient(_handler), new Uri("https://offline.example.org/"));

        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
        {
            return source.ReadAsync("manifest.json.sig", 100, CancellationToken.None);
        });
        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
        {
            return source.ReadAsync("manifest.json", 100, CancellationToken.None);
        });
        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
        {
            return offline.ReadAsync("manifest.json", 100, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Http_StopsReadingAtTheSizeLimit()
    {
        _handler.Add("/manifest.json", new byte[101]);
        _handler.Responses["/manifest.json.sig"] = () =>
        {
            var content = new StreamContent(new MemoryStream(new byte[101]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        var source = new HttpManifestSource(new HttpClient(_handler), new Uri("https://cdn.example.org/"));

        await Assert.ThrowsAsync<ManifestException>(() =>
        {
            return source.ReadAsync("manifest.json", 100, CancellationToken.None);
        });
        await Assert.ThrowsAsync<ManifestException>(() =>
        {
            return source.ReadAsync("manifest.json.sig", 100, CancellationToken.None);
        });
    }

    [Theory]
    [InlineData("../manifest.json")]
    [InlineData("assets/x.png")]
    [InlineData("launcher/CpLauncher-1.0.0.exe")]
    [InlineData("MANIFEST.JSON")]
    public async Task Sources_RefuseFilesOutsideTheDistributionLayout(string path)
    {
        var http = new HttpManifestSource(new HttpClient(_handler), new Uri("https://cdn.example.org/"));
        var folder = new FolderManifestSource(_fileSystem, @"C:\source");

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            return http.ReadAsync(path, 100, CancellationToken.None);
        });
        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            return folder.ReadAsync(path, 100, CancellationToken.None);
        });
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Folder_ReadsFilesAndReportsMissingOnesAsUnavailable()
    {
        string asset = "assets/" + new string('c', 64) + ".png";
        _fileSystem.Seed(@"C:\source\manifest.json", new byte[] { 7 });
        _fileSystem.Seed(@"C:\source\assets\" + new string('c', 64) + ".png", new byte[] { 8 });
        var source = new FolderManifestSource(_fileSystem, @"C:\source");

        Assert.Equal(new byte[] { 7 }, await source.ReadAsync("manifest.json", 10, CancellationToken.None));
        Assert.Equal(new byte[] { 8 }, await source.ReadAsync(asset, 10, CancellationToken.None));
        await Assert.ThrowsAsync<SourceUnavailableException>(() =>
        {
            return source.ReadAsync("manifest.json.sig", 10, CancellationToken.None);
        });
        await Assert.ThrowsAsync<ManifestException>(() =>
        {
            return source.ReadAsync("manifest.json", 0, CancellationToken.None);
        });
    }

    [Theory]
    [InlineData("https://cdn.example.org/cp/", typeof(HttpManifestSource))]
    [InlineData("http://localhost:8080", typeof(HttpManifestSource))]
    [InlineData(@"C:\Lineage2Dex\.modding\dist", typeof(FolderManifestSource))]
    [InlineData("file:///C:/dist", typeof(FolderManifestSource))]
    public void Factory_PicksTheSourceType(string configured, Type expected)
    {
        var factory = new ManifestSourceFactory(_fileSystem, new HttpClient(_handler));

        IManifestSource? source = factory.Create(configured);

        Assert.IsType(expected, source);
    }

    [Fact]
    public void Factory_ReturnsNullWhenNothingIsConfigured()
    {
        var factory = new ManifestSourceFactory(_fileSystem, new HttpClient(_handler));

        Assert.Null(factory.Create(null));
        Assert.Null(factory.Create("  "));
    }

    [Theory]
    [InlineData("dist")]
    [InlineData("ftp://example.org/")]
    [InlineData("https://cdn.example.org/cp/?token=1")]
    [InlineData("https://")]
    public void Factory_RejectsUnusableText(string configured)
    {
        var factory = new ManifestSourceFactory(_fileSystem, new HttpClient(_handler));

        Assert.Throws<FormatException>(() =>
        {
            factory.Create(configured);
        });
    }
}
