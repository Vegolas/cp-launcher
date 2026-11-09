using System.Text.Json.Nodes;
using CpLauncher.Services;
using CpLauncher.ViewModels;
using L2Mod.Core;
using L2Mod.Core.Effects;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

/// <summary>Packs with options (one checkbox per status or swap) and the effect packages in the dashboard.</summary>
public class EffectPackTests
{
    private readonly LauncherFixture _fixture = new LauncherFixture();
    private readonly byte[] _skillEffects = TestEffectPackages.BuildSkillEffects();

    public EffectPackTests()
    {
        _fixture.FileSystem.Seed(SkillEffectPath, _skillEffects);
        _fixture.Publish(ManifestWithEffects());
    }

    private static string SkillEffectPath
    {
        get
        {
            return Path.Combine(LauncherFixture.ClientDirectory, ClientFiles.GetRelativePath(ClientFiles.LineageSkillEffect));
        }
    }

    [Fact]
    public void PackWithOptions_ShowsAllSomeOrNoneAndSetsAllAtOnce()
    {
        int changes = 0;
        PackViewModel pack = CreateOptionPack(new[] { true, false, false }, () =>
        {
            changes++;
        });

        Assert.True(pack.HasOptions);
        Assert.Null(pack.IsChecked);
        Assert.True(pack.IsSelected);
        Assert.Equal("wybrano 1 z 3", pack.SizeText);
        Assert.Equal(new[] { "icons/1071" }, pack.SelectedIds);

        pack.IsChecked = true;

        Assert.Equal(1, changes);
        Assert.True(pack.IsChecked);
        Assert.Equal(new[] { "icons/1071", "icons/1074", "icons/1323" }, pack.SelectedIds);

        pack.Options[1].IsSelected = false;
        pack.Options[0].IsSelected = false;
        pack.Options[2].IsSelected = false;

        Assert.Equal(4, changes);
        Assert.False(pack.IsChecked);
        Assert.False(pack.IsSelected);
        Assert.Empty(pack.SelectedIds);
        Assert.Equal("wybrano 0 z 3", pack.SizeText);
    }

    [Fact]
    public void PackWithOptions_ClickOnAPartialCheckboxClearsAll_AndEditabilityReachesTheOptions()
    {
        PackViewModel pack = CreateOptionPack(new[] { true, true, false }, () =>
        {
        });

        pack.IsChecked = false;
        pack.IsEditable = false;

        Assert.All(pack.Options, option => Assert.False(option.IsSelected));
        Assert.All(pack.Options, option => Assert.False(option.IsEditable));
    }

    [Fact]
    public void PlainEffectPack_CountsEffectsNotIcons()
    {
        var pack = new PackViewModel(new Pack("arrow", "Strzałka", false, new IPatchOperation[] { new GoldClickArrowOperation() }), true, () =>
        {
        });

        Assert.Equal("1 efekt", pack.SizeText);
        Assert.True(pack.IsChecked);
        Assert.Equal(new[] { "arrow" }, pack.SelectedIds);
    }

    [Fact]
    public void Settings_StoreOptionsByPackAndOption_AndNewOptionsUseTheirDefault()
    {
        Manifest manifest = new Manifest(1, "1", true, null, new Dictionary<string, IReadOnlyList<string>>(), Array.Empty<Announcement>(), new[] { OptionPack() }, Array.Empty<OptionPreset>());
        Pack pack = manifest.Packs[0];
        var settings = new LauncherSettings();

        Assert.Equal(new[] { "icons/1323" }, settings.GetSelectedPackIds(manifest));

        LauncherSettings saved = settings.WithSelection(manifest, new[] { "icons/1071" });

        Assert.Equal(new Dictionary<string, bool> { ["icons/1071"] = true, ["icons/1074"] = false, ["icons/1323"] = false }, saved.PackSelection);
        Assert.True(saved.IsSelected(pack, pack.Options[0]));
        Assert.False(saved.IsSelected(pack, pack.Options[2]));
        Assert.Equal(new[] { "icons/1071" }, saved.GetSelectedPackIds(manifest));
    }

    [Fact]
    public void StatusTexts_LeaveOutEffectPackagesThatAreNotConcerned()
    {
        PatchFileStatus icon = Status(ClientFiles.IconUtx, PatchFileState.OurPatch, PendingAction.None, PatchReason.UpToDate, 2);
        PatchFileStatus untouched = Status(ClientFiles.LineageSkillEffect, PatchFileState.Unknown, PendingAction.None, PatchReason.UnknownVersion, 0);
        PatchFileStatus missing = Status(ClientFiles.LineageEffect, PatchFileState.Missing, PendingAction.None, PatchReason.FileMissing, 0);

        Assert.Equal("Zainstalowane zmiany CP.", StatusTexts.DescribeState(new[] { icon, untouched, missing }).Text);
        Assert.Equal("Zmiany są aktualne.", StatusTexts.DescribePending(new[] { icon, untouched, missing }));
    }

    [Fact]
    public void StatusTexts_NameTheEffectPackageThatNeedsAttention()
    {
        PatchFileStatus icon = Status(ClientFiles.IconUtx, PatchFileState.OurPatch, PendingAction.None, PatchReason.UpToDate, 2);
        PatchFileStatus pending = Status(ClientFiles.LineageSkillEffect, PatchFileState.Original, PendingAction.Install, PatchReason.NotInstalled, 3);
        PatchFileStatus unknown = Status(ClientFiles.LineageEffect, PatchFileState.Unknown, PendingAction.None, PatchReason.UnknownVersion, 1);

        StatusText state = StatusTexts.DescribeState(new[] { icon, pending, unknown });
        string? text = StatusTexts.DescribePending(new[] { icon, pending, unknown });

        Assert.Equal(StatusSeverity.Warning, state.Severity);
        Assert.StartsWith("Nieznana wersja LineageEffect.u", state.Text);
        Assert.Equal("Wybrane paczki nie są jeszcze zainstalowane – kliknij „Zastosuj”.", text);
    }

    [Fact]
    public void StatusTexts_DescribeTheResultOfEveryChangedFile()
    {
        PatchFileStatus icon = Status(ClientFiles.IconUtx, PatchFileState.OurPatch, PendingAction.None, PatchReason.UpToDate, 2);
        PatchFileStatus effects = Status(ClientFiles.LineageSkillEffect, PatchFileState.Original, PendingAction.Install, PatchReason.NotInstalled, 3);
        PatchFileStatus arrow = Status(ClientFiles.LineageEffect, PatchFileState.OurPatch, PendingAction.Restore, PatchReason.NothingSelected, 0);
        var results = new[]
        {
            Result(ClientFiles.IconUtx, PatchOutcome.Unchanged, PatchReason.UpToDate),
            Result(ClientFiles.LineageSkillEffect, PatchOutcome.Installed, PatchReason.NotInstalled),
            Result(ClientFiles.LineageEffect, PatchOutcome.Restored, PatchReason.NothingSelected),
        };

        StatusText text = StatusTexts.DescribeApply(results, new[] { icon, effects, arrow });

        Assert.Equal(StatusSeverity.Success, text.Severity);
        Assert.Equal("Zainstalowano zmiany w LineageSkillEffect.u (3 efekty). Przywrócono oryginalny LineageEffect.u.", text.Text);
        Assert.Equal(
            "Przywrócono oryginalne pliki: Icon.utx, LineageSkillEffect.u.",
            StatusTexts.DescribeRestore(new[]
            {
                Result(ClientFiles.IconUtx, PatchOutcome.Restored, PatchReason.RestoreRequested),
                Result(ClientFiles.LineageSkillEffect, PatchOutcome.Restored, PatchReason.RestoreRequested),
                Result(ClientFiles.LineageEffect, PatchOutcome.Skipped, PatchReason.NoBackup),
            }).Text);
    }

    [Fact]
    public async Task Dashboard_ShowsOptions_AndApplyInstallsTheSwapIntoLineageSkillEffect()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        PackViewModel swaps = dashboard.Packs.Single(pack => pack.Id == "swaps");
        Assert.Equal(new[] { "Hydro Blast ↔ Aqua Swirl" }, swaps.Options.Select(option => option.Name));
        Assert.False(swaps.IsSelected);
        await dashboard.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(_skillEffects, _fixture.FileSystem.ReadAllBytes(SkillEffectPath));

        swaps.Options[0].IsSelected = true;
        await WaitForAsync(() =>
        {
            return dashboard.FileStatuses.Any(status => status.FileName == ClientFiles.LineageSkillEffect && status.Pending == PendingAction.Install);
        });
        Assert.True(dashboard.HasPendingChanges);
        Assert.Equal("Wybrane paczki nie są jeszcze zainstalowane – kliknij „Zastosuj”.", dashboard.PendingText);
        await dashboard.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Zainstalowano zmiany w LineageSkillEffect.u (1 efekt).", dashboard.LastAction?.Text);
        Assert.NotEqual(_skillEffects, _fixture.FileSystem.ReadAllBytes(SkillEffectPath));
        Assert.False(dashboard.HasPendingChanges);
        LauncherSettings saved = new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Load().Settings;
        Assert.True(saved.PackSelection["swaps/1235-1175"]);

        await dashboard.RestoreOriginalCommand.ExecuteAsync(null);

        Assert.Equal("Przywrócono oryginalne pliki: Icon.utx, LineageSkillEffect.u.", dashboard.LastAction?.Text);
        Assert.Equal(_skillEffects, _fixture.FileSystem.ReadAllBytes(SkillEffectPath));
    }

    private JsonObject ManifestWithEffects()
    {
        JsonObject manifest = _fixture.Manifest();
        manifest["supportedOriginals"]![ClientFiles.LineageSkillEffect] = new JsonArray(ContentHash.Sha256Hex(_skillEffects));
        manifest["packs"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "swaps",
            ["name"] = "Zamiana efektów",
            ["default"] = false,
            ["options"] = new JsonArray(new JsonObject
            {
                ["id"] = "1235-1175",
                ["name"] = "Hydro Blast ↔ Aqua Swirl",
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["op"] = "effect-swap",
                    ["file"] = ClientFiles.LineageSkillEffect,
                    ["skills"] = new JsonArray(1235, 1175),
                }),
            }),
        });
        return manifest;
    }

    private static Pack OptionPack()
    {
        return Pack.WithOptions("icons", "Ikony nad celem", false, new[]
        {
            new PackOption("1071", "Surrender to Water", false, new IPatchOperation[] { new TargetIconOperation(1071, 1071, 5) }),
            new PackOption("1074", "Surrender to Wind", false, new IPatchOperation[] { new TargetIconOperation(1074, 1074, 5) }),
            new PackOption("1323", "Noblesse Blessing", true, new IPatchOperation[] { new TargetIconOperation(1323, 1048, 5) }),
        });
    }

    private static PackViewModel CreateOptionPack(IReadOnlyList<bool> selection, Action changed)
    {
        return new PackViewModel(OptionPack(), false, selection, changed);
    }

    private static PatchFileStatus Status(string file, PatchFileState state, PendingAction pending, PatchReason reason, int operations)
    {
        return new PatchFileStatus(file, state, null, false, pending, string.Empty)
        {
            Reason = reason,
            OperationCount = operations,
        };
    }

    private static PatchFileResult Result(string file, PatchOutcome outcome, PatchReason reason)
    {
        return new PatchFileResult(file, outcome, string.Empty, Array.Empty<string>())
        {
            Reason = reason,
        };
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
