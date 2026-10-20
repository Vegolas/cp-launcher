using CpLauncher.Services;
using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

public class AssetAndPreviewTests
{
    private readonly LauncherFixture _fixture = new LauncherFixture();
    private readonly AssetCache _cache;
    private readonly FolderManifestSource _source;
    private readonly Manifest _manifest;

    public AssetAndPreviewTests()
    {
        _cache = new AssetCache(_fixture.FileSystem, _fixture.Paths);
        _source = new FolderManifestSource(_fixture.FileSystem, LauncherFixture.SourceDirectory);
        _fixture.Publish(_fixture.Manifest());
        _manifest = ManifestParser.Parse(TestManifests.Serialize(_fixture.Manifest())).Manifest;
    }

    [Fact]
    public async Task EnsureAsync_DownloadsMissingOverlaysIntoTheStoreLayout()
    {
        IReadOnlyList<OverlayOperation> overlays = AssetCache.GetOverlays(_manifest, new[] { "marks" });

        AssetSyncResult result = await _cache.EnsureAsync(overlays, _source, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(TestManifests.OverlayPng, _cache.Store.ReadAsset(TestManifests.OverlayAssetPath));
        Assert.Empty(AssetCache.GetOverlays(_manifest, new[] { "badges", "tags" }));
    }

    [Fact]
    public async Task EnsureAsync_Offline_ReportsMissingImagesButUsesCachedOnes()
    {
        IReadOnlyList<OverlayOperation> overlays = AssetCache.GetOverlays(_manifest, new[] { "marks" });

        AssetSyncResult offline = await _cache.EnsureAsync(overlays, null, CancellationToken.None);
        await _cache.EnsureAsync(overlays, _source, CancellationToken.None);
        AssetSyncResult cached = await _cache.EnsureAsync(overlays, null, CancellationToken.None);

        Assert.Equal(new[] { TestManifests.OverlayAssetPath }, offline.Missing);
        Assert.True(cached.IsComplete);
    }

    [Fact]
    public async Task EnsureAsync_RejectsAnImageThatDoesNotMatchTheSignedHash()
    {
        _fixture.FileSystem.Seed(Path.Combine(LauncherFixture.SourceDirectory, "assets", $"{TestManifests.OverlayHash}.png"), new byte[] { 1, 2, 3 });
        IReadOnlyList<OverlayOperation> overlays = AssetCache.GetOverlays(_manifest, new[] { "marks" });

        AssetSyncResult result = await _cache.EnsureAsync(overlays, _source, CancellationToken.None);

        Assert.Single(result.Errors);
        Assert.False(_fixture.FileSystem.FileExists(_fixture.Paths.GetCachedAssetPath(TestManifests.OverlayAssetPath)));
    }

    [Fact]
    public async Task Render_DrawsEachPackOnTheMembersOwnIconUtx()
    {
        await _cache.EnsureAsync(AssetCache.GetOverlays(_manifest, new[] { "marks" }), _source, CancellationToken.None);
        var renderer = new PackPreviewRenderer();

        IReadOnlyList<PackPreview> previews = renderer.Render(_fixture.Original, _manifest, _cache.Store);

        Assert.Equal(new[] { "badges", "tags", "marks" }, previews.Select(preview => preview.PackId));
        Assert.All(previews, preview =>
        {
            Assert.Equal(0, preview.Failed);
            Assert.Single(preview.Icons);
        });
        PreviewIcon tag = previews[1].Icons[0];
        Assert.Equal("skill0102", tag.Target);
        RgbaImage original = TestIcons.OpenDefault().Decode(TestIcons.OpenDefault().FindTexture("skill0102"));
        Assert.NotEqual(TestIcons.Pixel(original, 1, 1), TestIcons.Pixel(tag.Image, 1, 1));
        Assert.Equal(TestIcons.Pixel(original, 20, 20), TestIcons.Pixel(tag.Image, 20, 20));
    }

    [Fact]
    public void Render_CountsOperationsThatCannotBeDrawn()
    {
        var renderer = new PackPreviewRenderer();

        IReadOnlyList<PackPreview> previews = renderer.Render(_fixture.Original, _manifest, _cache.Store);

        Assert.Equal(1, previews[2].Failed);
        Assert.Empty(previews[2].Icons);
    }

    [Fact]
    public void Render_UnreadablePackage_FailsEveryPackWithoutThrowing()
    {
        var renderer = new PackPreviewRenderer();

        IReadOnlyList<PackPreview> previews = renderer.Render(new byte[] { 1, 2, 3 }, _manifest, _cache.Store);

        Assert.All(previews, preview =>
        {
            Assert.Empty(preview.Icons);
        });
    }

    [Fact]
    public async Task Previews_AreDrawnOnTheBackedUpOriginalWhileOurPatchIsInstalled()
    {
        CpLauncher.ViewModels.DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        RgbaImage before = dashboard.Packs[1].Thumbnails[0].Image;
        await dashboard.ApplyCommand.ExecuteAsync(null);

        await dashboard.RefreshCommand.ExecuteAsync(null);

        Assert.NotEqual(_fixture.Original, _fixture.ReadIcon());
        Assert.Equal(before.Pixels, dashboard.Packs[1].Thumbnails[0].Image.Pixels);
    }

    [Fact]
    public async Task Previews_AreNotDrawnWithoutAnOriginal()
    {
        CpLauncher.ViewModels.DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);
        _fixture.FileSystem.Seed(_fixture.InstallerOptions.GetBackupPath(ClientFiles.IconUtx, _fixture.OriginalHash), new byte[] { 9 });

        CpLauncher.ViewModels.DashboardViewModel damaged = await _fixture.CreateDashboardAsync(null);
        _fixture.FileSystem.DeleteFile(_fixture.IconPath);
        CpLauncher.ViewModels.DashboardViewModel missing = await _fixture.CreateDashboardAsync(null);

        Assert.All(damaged.Packs, pack =>
        {
            Assert.Empty(pack.Thumbnails);
        });
        Assert.All(missing.Packs, pack =>
        {
            Assert.Empty(pack.Thumbnails);
        });
    }

    [Fact]
    public void PackPreview_CountsTheIconsBeyondTheStripAndNotesOnlyTheFailedOnes()
    {
        IIconOperation[] operations = Enumerable.Range(0, 22)
            .Select(index => (IIconOperation)new TagOperation("Icon.utx", $"icon.skill{index}", new Rgb(255, 0, 0), 8, IconCorner.TopLeft))
            .ToArray();
        var pack = new CpLauncher.ViewModels.PackViewModel(new Pack("tags", "Tagi", true, operations), true, () =>
        {
        });
        PreviewIcon[] shown = Enumerable.Range(0, PackPreviewRenderer.MaxIconsPerPack)
            .Select(index => new PreviewIcon($"icon.skill{index}", new RgbaImage(32, 32)))
            .ToArray();

        pack.SetPreview(new PackPreview("tags", shown, 2));
        Assert.Equal("+14", pack.OverflowText);
        Assert.Equal("2 bez podglądu (brak ikony w Twoim Icon.utx lub obrazka)", pack.PreviewNote);

        pack.SetPreview(new PackPreview("tags", shown, 16));
        Assert.Null(pack.OverflowText);
    }
}
