using L2Mod.Core.Badges;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Tests;

/// <summary>The installer on the effect packages: the same state machine as Icon.utx, per file.</summary>
public class EffectInstallerTests
{
    private const string Water = "icons/1071";
    private const string Noblesse = "icons/1323";
    private const string Swap = "swaps/1235-1175";
    private const string Arrow = "arrow";
    private const string Badges = "badges";

    private readonly InMemoryFileSystem _fileSystem = new InMemoryFileSystem();
    private readonly InMemoryAssetStore _assets = new InMemoryAssetStore();
    private readonly InstallerOptions _options = new InstallerOptions(@"C:\client", @"C:\data");
    private readonly byte[] _icons = TestEffectPackages.BuildIconUtx();
    private readonly byte[] _skillEffects = TestEffectPackages.BuildSkillEffects();
    private readonly byte[] _effects = TestEffectPackages.BuildEffects();
    private readonly PatchInstaller _installer;

    public EffectInstallerTests()
    {
        _fileSystem.Seed(ClientPath(ClientFiles.IconUtx), _icons);
        _fileSystem.Seed(ClientPath(ClientFiles.LineageSkillEffect), _skillEffects);
        _fileSystem.Seed(ClientPath(ClientFiles.LineageEffect), _effects);
        _installer = new PatchInstaller(_options, _fileSystem, new FakeProcessMonitor());
    }

    [Fact]
    public void Apply_InstallsTheSelectedEffectsAndLeavesTheOtherFilesAlone()
    {
        Manifest manifest = CreateManifest();
        IReadOnlyList<PatchFileStatus> before = _installer.GetStatus(manifest, new[] { Water, Swap });
        Assert.Equal(new[] { PendingAction.None, PendingAction.Install, PendingAction.None }, before.Select(status => status.Pending));
        Assert.Equal(new[] { 0, 2, 0 }, before.Select(status => status.OperationCount));

        IReadOnlyList<PatchFileResult> results = _installer.Apply(manifest, new[] { Water, Swap }, _assets);

        Assert.Equal(new[] { PatchOutcome.Unchanged, PatchOutcome.Installed, PatchOutcome.Unchanged }, results.Select(result => result.Outcome));
        byte[] expected = EffectComposer.Build(ClientFiles.LineageSkillEffect, _skillEffects, new IEffectOperation[] { new TargetIconOperation(1071, 1071, 5), new EffectSwapOperation(1235, 1175) }, () =>
        {
            return TestIcons.Open(_icons);
        }).FileBytes;
        Assert.Equal(expected, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageSkillEffect)));
        Assert.Equal(_icons, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.IconUtx)));
        Assert.Equal(_effects, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageEffect)));
        Assert.True(_fileSystem.FileExists(_options.GetBackupPath(ClientFiles.LineageSkillEffect, ContentHash.Sha256Hex(_skillEffects))));
        Assert.All(_installer.GetStatus(manifest, new[] { Water, Swap }), status => Assert.Equal(PendingAction.None, status.Pending));
    }

    [Fact]
    public void Apply_TakesTheIconsFromTheOriginalIconUtxEvenWhenOurPatchIsInstalled()
    {
        Manifest manifest = CreateManifest();
        _installer.Apply(manifest, new[] { Badges }, _assets);
        Assert.NotEqual(_icons, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.IconUtx)));

        IReadOnlyList<PatchFileResult> results = _installer.Apply(manifest, new[] { Badges, Water }, _assets);

        Assert.Equal(PatchOutcome.Installed, results[1].Outcome);
        byte[] expected = EffectComposer.Build(ClientFiles.LineageSkillEffect, _skillEffects, new IEffectOperation[] { new TargetIconOperation(1071, 1071, 5) }, () =>
        {
            return TestIcons.Open(_icons);
        }).FileBytes;
        Assert.Equal(expected, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageSkillEffect)));
    }

    [Fact]
    public void Apply_RebuildsFromTheBackupWhenOptionsChange_AndRestoresWhenNoneAreLeft()
    {
        Manifest manifest = CreateManifest();
        _installer.Apply(manifest, new[] { Water }, _assets);

        Assert.Equal(PendingAction.Rebuild, _installer.GetStatus(manifest, new[] { Water, Noblesse })[1].Pending);
        Assert.Equal(PatchOutcome.Rebuilt, _installer.Apply(manifest, new[] { Water, Noblesse }, _assets)[1].Outcome);
        Assert.Equal(PendingAction.Restore, _installer.GetStatus(manifest, Array.Empty<string>())[1].Pending);
        Assert.Equal(PatchOutcome.Restored, _installer.Apply(manifest, Array.Empty<string>(), _assets)[1].Outcome);

        Assert.Equal(_skillEffects, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageSkillEffect)));
    }

    [Fact]
    public void Apply_InstallsTheGoldArrowIntoLineageEffect_AndRestoreOriginalPutsEverythingBack()
    {
        Manifest manifest = CreateManifest();

        IReadOnlyList<PatchFileResult> applied = _installer.Apply(manifest, new[] { Badges, Swap, Arrow }, _assets);
        IReadOnlyList<PatchFileResult> restored = _installer.Restore();

        Assert.All(applied, result => Assert.Equal(PatchOutcome.Installed, result.Outcome));
        Assert.All(restored, result => Assert.Equal(PatchOutcome.Restored, result.Outcome));
        Assert.Equal(_icons, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.IconUtx)));
        Assert.Equal(_skillEffects, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageSkillEffect)));
        Assert.Equal(_effects, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageEffect)));
    }

    [Fact]
    public void Apply_FailsTheIconsWithoutAnOriginalIconUtx_AndTouchesNothing()
    {
        Manifest manifest = CreateManifest();
        _fileSystem.Seed(ClientPath(ClientFiles.IconUtx), TestIcons.BuildIconUtx());

        IReadOnlyList<PatchFileResult> results = _installer.Apply(manifest, new[] { Water }, _assets);

        Assert.Equal(PatchReason.UnknownVersion, results[0].Reason);
        Assert.Equal(PatchOutcome.Failed, results[1].Outcome);
        Assert.Contains("no original Icon.utx", results[1].Message);
        Assert.Equal(_skillEffects, _fileSystem.ReadAllBytes(ClientPath(ClientFiles.LineageSkillEffect)));
    }

    [Fact]
    public void Status_LeavesAnUnknownEffectPackageAlone()
    {
        _fileSystem.Seed(ClientPath(ClientFiles.LineageSkillEffect), TestEffectPackages.BuildSkillEffects(new[] { 1071 }));

        PatchFileStatus status = _installer.GetStatus(CreateManifest(), new[] { Water })[1];
        PatchFileResult result = _installer.Apply(CreateManifest(), new[] { Water }, _assets)[1];

        Assert.Equal((PatchFileState.Unknown, PendingAction.None, 1), (status.State, status.Pending, status.OperationCount));
        Assert.Equal(PatchOutcome.Skipped, result.Outcome);
    }

    private static string ClientPath(string file)
    {
        return Path.Combine(@"C:\client", ClientFiles.GetRelativePath(file));
    }

    private Manifest CreateManifest()
    {
        var packs = new List<Pack>
        {
            new Pack(Badges, "Badges", true, new IPatchOperation[]
            {
                new BadgeOperation(ClientFiles.IconUtx, "skill1071", "skill1048", new BadgeStyle(16, IconCorner.BottomRight, 1)),
            }),
            Pack.WithOptions("icons", "Icons", false, new[]
            {
                new PackOption("1071", "Surrender to Water", false, new IPatchOperation[] { new TargetIconOperation(1071, 1071, 5) }),
                new PackOption("1323", "Noblesse Blessing", false, new IPatchOperation[] { new TargetIconOperation(1323, 1048, 5) }),
            }),
            Pack.WithOptions("swaps", "Swaps", false, new[]
            {
                new PackOption("1235-1175", "Hydro Blast ↔ Aqua Swirl", false, new IPatchOperation[] { new EffectSwapOperation(1235, 1175) }),
            }),
            new Pack(Arrow, "Arrow", false, new IPatchOperation[] { new GoldClickArrowOperation() }),
        };
        var originals = new Dictionary<string, IReadOnlyList<string>>
        {
            [ClientFiles.IconUtx] = new[] { ContentHash.Sha256Hex(_icons) },
            [ClientFiles.LineageSkillEffect] = new[] { ContentHash.Sha256Hex(_skillEffects) },
            [ClientFiles.LineageEffect] = new[] { ContentHash.Sha256Hex(_effects) },
        };
        return new Manifest(1, "test", true, null, originals, Array.Empty<Announcement>(), packs, Array.Empty<OptionPreset>());
    }
}
