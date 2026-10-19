using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Tests;

public class PatchInstallerTests
{
    private const string Icon = "Icon.utx";
    private static readonly string[] Tags = { "tags" };
    private static readonly string[] TagsAndBadges = { "tags", "badges" };

    private readonly InMemoryFileSystem _fileSystem = new InMemoryFileSystem();
    private readonly FakeProcessMonitor _processes = new FakeProcessMonitor();
    private readonly InMemoryAssetStore _assets = new InMemoryAssetStore();
    private readonly InstallerOptions _options = new InstallerOptions(@"C:\client", @"C:\data");
    private readonly byte[] _original = TestIcons.BuildIconUtx();
    private readonly PatchInstaller _installer;

    public PatchInstallerTests()
    {
        _fileSystem.Seed(ClientPath, _original);
        _installer = new PatchInstaller(_options, _fileSystem, _processes);
    }

    private string ClientPath
    {
        get
        {
            return Path.Combine(@"C:\client", "SysTextures", "Icon.utx");
        }
    }

    private string OriginalHash
    {
        get
        {
            return ContentHash.Sha256Hex(_original);
        }
    }

    [Fact]
    public void Apply_OnSupportedOriginal_BacksUpAndInstallsTheVerifiedPatch()
    {
        Manifest manifest = CreateManifest();
        Assert.Equal(PendingAction.Install, Single(_installer.GetStatus(manifest, Tags)).Pending);

        PatchFileResult result = Single(_installer.Apply(manifest, Tags, _assets));

        Assert.Equal(PatchOutcome.Installed, result.Outcome);
        byte[] installed = _fileSystem.ReadAllBytes(ClientPath);
        Assert.Equal(Build(Tags), installed);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(_options.GetBackupPath(Icon, OriginalHash)));
        Assert.True(_fileSystem.FileExists(_options.StatePath));
        Assert.DoesNotContain(_fileSystem.WrittenPaths, path => path.EndsWith(VerifiedWrite.TemporarySuffix) && _fileSystem.FileExists(path));
        PatchFileStatus status = Single(_installer.GetStatus(manifest, Tags));
        Assert.Equal((PatchFileState.OurPatch, PendingAction.None, true), (status.State, status.Pending, status.HasBackup));
    }

    [Fact]
    public void Apply_LeavesAnUpToDatePatchAlone()
    {
        Manifest manifest = CreateManifest();
        _installer.Apply(manifest, Tags, _assets);
        _fileSystem.WrittenPaths.Clear();

        PatchFileResult result = Single(_installer.Apply(manifest, Tags, _assets));

        Assert.Equal(PatchOutcome.Unchanged, result.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Fact]
    public void Apply_RebuildsFromTheOriginalWhenTheSelectionChanges()
    {
        Manifest manifest = CreateManifest();
        _installer.Apply(manifest, Tags, _assets);
        Assert.Equal(PendingAction.Rebuild, Single(_installer.GetStatus(manifest, TagsAndBadges)).Pending);

        PatchFileResult result = Single(_installer.Apply(manifest, TagsAndBadges, _assets));

        Assert.Equal(PatchOutcome.Rebuilt, result.Outcome);
        Assert.Equal(Build(TagsAndBadges), _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_RebuildsWhenTheRecipesChange()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        Manifest changed = CreateManifest(tagColor: "#FF9900");

        PatchFileResult result = Single(_installer.Apply(changed, Tags, _assets));

        Assert.Equal(PatchOutcome.Rebuilt, result.Outcome);
        Assert.Equal(Build(Tags, changed), _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_RestoresTheOriginalWhenNoPackIsSelected()
    {
        Manifest manifest = CreateManifest();
        _installer.Apply(manifest, Tags, _assets);

        PatchFileResult result = Single(_installer.Apply(manifest, Array.Empty<string>(), _assets));

        Assert.Equal(PatchOutcome.Restored, result.Outcome);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(ClientPath));
        Assert.Equal(PatchFileState.Original, Single(_installer.GetStatus(manifest, Array.Empty<string>())).State);
    }

    [Fact]
    public void KillSwitch_RestoresTheOriginalWhereOurPatchIsInstalled()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);

        PatchFileResult result = Single(_installer.Apply(CreateManifest(enabled: false), Tags, _assets));

        Assert.Equal(PatchOutcome.Restored, result.Outcome);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void KillSwitch_LeavesTheOriginalAlone()
    {
        PatchFileResult result = Single(_installer.Apply(CreateManifest(enabled: false), Tags, _assets));

        Assert.Equal(PatchOutcome.Unchanged, result.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Fact]
    public void KillSwitch_LeavesAnUnknownVersionAlone()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        byte[] unknown = MakeUnknownVersion();
        _fileSystem.WrittenPaths.Clear();

        PatchFileResult result = Single(_installer.Apply(CreateManifest(enabled: false), Tags, _assets));

        Assert.Equal(PatchOutcome.Skipped, result.Outcome);
        Assert.Equal(unknown, _fileSystem.ReadAllBytes(ClientPath));
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Fact]
    public void Apply_DoesNothingToAnUnknownVersion()
    {
        byte[] unknown = MakeUnknownVersion();

        PatchFileResult result = Single(_installer.Apply(CreateManifest(), Tags, _assets));

        Assert.Equal(PatchOutcome.Skipped, result.Outcome);
        Assert.Contains("Unknown version of Icon.utx", result.Message);
        Assert.Equal(unknown, _fileSystem.ReadAllBytes(ClientPath));
        Assert.Empty(_fileSystem.WrittenPaths);
        PatchFileStatus status = Single(_installer.GetStatus(CreateManifest(), Tags));
        Assert.Equal((PatchFileState.Unknown, false), (status.State, status.HasBackup));
    }

    [Fact]
    public void Restore_PutsTheBackupBackOverAnUnknownVersion()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        MakeUnknownVersion();
        PatchFileStatus status = Single(_installer.GetStatus(CreateManifest(), Tags));
        Assert.Equal((PatchFileState.Unknown, true), (status.State, status.HasBackup));
        Assert.Contains("can be restored", status.Message);

        PatchFileResult result = Single(_installer.Restore());

        Assert.Equal(PatchOutcome.Restored, result.Outcome);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Restore_PutsTheOriginalBackOverOurPatch()
    {
        _installer.Apply(CreateManifest(), TagsAndBadges, _assets);

        PatchFileResult result = Single(_installer.Restore());

        Assert.Equal(PatchOutcome.Restored, result.Outcome);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(ClientPath));
        Assert.Equal(PatchOutcome.Unchanged, Single(_installer.Restore()).Outcome);
    }

    [Fact]
    public void Restore_WithoutBackupDoesNothing()
    {
        MakeUnknownVersion();

        PatchFileResult result = Single(_installer.Restore());

        Assert.Equal(PatchOutcome.Skipped, result.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Theory]
    [InlineData("L2")]
    [InlineData("l2.bin")]
    public void ApplyAndRestore_AreBlockedWhileTheGameRuns(string process)
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        byte[] patched = _fileSystem.ReadAllBytes(ClientPath);
        _processes.Running.Add(process);
        _fileSystem.WrittenPaths.Clear();

        PatchFileResult apply = Single(_installer.Apply(CreateManifest(), TagsAndBadges, _assets));
        PatchFileResult restore = Single(_installer.Restore());

        Assert.Equal(PatchOutcome.Blocked, apply.Outcome);
        Assert.Equal(PatchOutcome.Blocked, restore.Outcome);
        Assert.Contains("close it first", apply.Message);
        Assert.Empty(_fileSystem.WrittenPaths);
        Assert.Equal(patched, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_ReportsAMissingFile()
    {
        _fileSystem.DeleteFile(ClientPath);

        PatchFileResult result = Single(_installer.Apply(CreateManifest(), Tags, _assets));

        Assert.Equal(PatchOutcome.Skipped, result.Outcome);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Apply_KeepsOurPatchWhenTheManifestSchemaIsTooNew()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        byte[] patched = _fileSystem.ReadAllBytes(ClientPath);
        Manifest newer = CreateManifest() with { Schema = 2, Packs = Array.Empty<Pack>() };

        PatchFileResult kept = Single(_installer.Apply(newer, Tags, _assets));
        PatchFileResult switchedOff = Single(_installer.Apply(newer with { Enabled = false }, Tags, _assets));

        Assert.Equal(PatchOutcome.Unchanged, kept.Outcome);
        Assert.Contains("launcher is updated", kept.Message);
        Assert.Equal(PatchOutcome.Restored, switchedOff.Outcome);
        Assert.NotEqual(patched, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_DoesNotRebuildFromAnOriginalTheManifestNoLongerSupports()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        byte[] patched = _fileSystem.ReadAllBytes(ClientPath);
        Manifest moved = CreateManifest() with
        {
            SupportedOriginals = new Dictionary<string, IReadOnlyList<string>> { [Icon] = new[] { new string('d', 64) } },
        };

        PatchFileResult result = Single(_installer.Apply(moved, TagsAndBadges, _assets));

        Assert.Equal(PatchOutcome.Unchanged, result.Outcome);
        Assert.Contains("no longer supports", result.Message);
        Assert.Equal(patched, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_FailsWithoutTouchingTheClientWhenTheBackupIsDamaged()
    {
        _installer.Apply(CreateManifest(), Tags, _assets);
        byte[] patched = _fileSystem.ReadAllBytes(ClientPath);
        _fileSystem.Seed(_options.GetBackupPath(Icon, OriginalHash), new byte[] { 1, 2, 3 });

        PatchFileResult result = Single(_installer.Apply(CreateManifest(), TagsAndBadges, _assets));

        Assert.Equal(PatchOutcome.Failed, result.Outcome);
        Assert.Contains("damaged", result.Message);
        Assert.Equal(patched, _fileSystem.ReadAllBytes(ClientPath));
    }

    [Fact]
    public void Apply_InstallsOverlaysFromVerifiedAssetsOnly()
    {
        var mark = new RgbaImage(8, 8);
        mark.FillRectangle(new PixelRect(0, 0, 8, 8), 0, 255, 0);
        byte[] png = PngCodec.Encode(mark);
        string path = _assets.Add(png);
        var overlay = new Pack("marks", "Marks", false, new IIconOperation[]
        {
            new OverlayOperation(Icon, "skill0365", path, ContentHash.Sha256Hex(png), IconCorner.BottomRight),
        });
        Manifest manifest = CreateManifest() with { Packs = new[] { overlay } };
        _assets.Replace(path, PngCodec.Encode(TestPackageBuilder.SolidImage(8, 8, 255, 0, 0)));

        PatchFileResult tampered = Single(_installer.Apply(manifest, new[] { "marks" }, _assets));

        Assert.Equal(PatchOutcome.Failed, tampered.Outcome);
        Assert.Equal(_original, _fileSystem.ReadAllBytes(ClientPath));

        _assets.Replace(path, png);
        PatchFileResult installed = Single(_installer.Apply(manifest, new[] { "marks" }, _assets));

        Assert.Equal(PatchOutcome.Installed, installed.Outcome);
    }

    [Fact]
    public void DirectoryAssetStore_ReadsOnlyAssetPaths()
    {
        string hash = new string('e', 64);
        _fileSystem.Seed(Path.Combine(@"C:\data\cache", "assets", hash + ".png"), new byte[] { 7 });
        var store = new DirectoryAssetStore(_fileSystem, @"C:\data\cache");

        Assert.Equal(new byte[] { 7 }, store.ReadAsset($"assets/{hash}.png"));
        Assert.Throws<ArgumentException>(() => store.ReadAsset("assets/../state.json"));
        Assert.Throws<ArgumentException>(() => store.ReadAsset($"../assets/{hash}.png"));
    }

    private byte[] MakeUnknownVersion()
    {
        byte[] unknown = (byte[])_original.Clone();
        unknown[unknown.Length - 1] ^= 0xFF;
        _fileSystem.Seed(ClientPath, unknown);
        return unknown;
    }

    private Manifest CreateManifest(bool enabled = true, string tagColor = "#E02020")
    {
        var packs = new[]
        {
            new Pack("tags", "Tags", true, new IIconOperation[]
            {
                new TagOperation(Icon, "skill0102", Rgb.ParseHex(tagColor), 8, IconCorner.TopLeft),
            }),
            new Pack("badges", "Badges", true, new IIconOperation[]
            {
                new BadgeOperation(Icon, "skill0365", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 1)),
                new BadgeOperation(Icon, "skill0102", "skill1059", new BadgeStyle(20, IconCorner.BottomRight, 1)),
            }),
        };
        var originals = new Dictionary<string, IReadOnlyList<string>> { [Icon] = new[] { OriginalHash } };
        return new Manifest(1, "test", enabled, null, originals, Array.Empty<Announcement>(), packs, Array.Empty<OptionPreset>());
    }

    private byte[] Build(IReadOnlyCollection<string> packs, Manifest? manifest = null)
    {
        Manifest source = manifest ?? CreateManifest();
        IconPackage package = TestIcons.Open(_original);
        return OperationComposer.Build(package, source.GetOperations(Icon, packs), _assets).Replacement.FileBytes;
    }

    private static T Single<T>(IReadOnlyList<T> items)
    {
        Assert.Single(items);
        return items[0];
    }
}
