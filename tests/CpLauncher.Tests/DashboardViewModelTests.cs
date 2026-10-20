using System.ComponentModel;
using System.Text.Json.Nodes;
using CpLauncher.Services;
using CpLauncher.ViewModels;
using L2Mod.Core;
using L2Mod.Core.Installation;

namespace CpLauncher.Tests;

public class DashboardViewModelTests
{
    private readonly LauncherFixture _fixture = new LauncherFixture();

    public DashboardViewModelTests()
    {
        _fixture.Publish(_fixture.Manifest());
    }

    [Fact]
    public async Task Load_ShowsManifestPacksPresetsAndAnnouncements()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal("2026.10.20.1", dashboard.ManifestVersionText);
        Assert.Equal("pobrany teraz", dashboard.ManifestOriginText);
        Assert.Empty(dashboard.Banners);
        Assert.Equal(new[] { "badges", "tags", "marks" }, dashboard.Packs.Select(pack => pack.Id));
        Assert.Equal(new[] { true, true, false }, dashboard.Packs.Select(pack => pack.IsSelected));
        Assert.All(dashboard.Packs, pack =>
        {
            Assert.True(pack.IsEditable);
            Assert.Single(pack.Thumbnails);
        });
        Assert.Equal("Masowe PvP", Assert.Single(dashboard.Presets).Name);
        Assert.Equal(new[] { "Start", "Starsze" }, dashboard.Announcements.Select(item => item.Title));
        Assert.Equal("<b>Zwykły</b> tekst.", dashboard.Announcements[0].Text);
        Assert.Equal("20.10.2026", dashboard.Announcements[0].DateText);
        Assert.Equal(PatchFileState.Original, dashboard.IconStatus?.State);
        Assert.Equal(PendingAction.Install, dashboard.IconStatus?.Pending);
        Assert.Contains("nie są jeszcze zainstalowane", dashboard.PendingText);
        Assert.True(dashboard.ApplyCommand.CanExecute(null));
        Assert.False(dashboard.RestoreOriginalCommand.CanExecute(null));
        Assert.Null(dashboard.UpdateText);
        Assert.DoesNotContain(_fixture.FileSystem.WrittenPaths, IsClientPath);
    }

    [Fact]
    public async Task Apply_InstallsTheSelectedPacksAndRemembersTheChoice()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        await dashboard.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Success, dashboard.LastAction?.Severity);
        Assert.Equal("Zainstalowano zmiany (2 ikony).", dashboard.LastAction?.Text);
        Assert.Equal(PatchFileState.OurPatch, dashboard.IconStatus?.State);
        Assert.Equal(PendingAction.None, dashboard.IconStatus?.Pending);
        Assert.Equal("Zmiany są aktualne.", dashboard.PendingText);
        Assert.NotEqual(_fixture.Original, _fixture.ReadIcon());
        Assert.Equal(_fixture.Original, _fixture.FileSystem.ReadAllBytes(_fixture.InstallerOptions.GetBackupPath(ClientFiles.IconUtx, _fixture.OriginalHash)));
        LauncherSettings saved = new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Load().Settings;
        Assert.Equal(new Dictionary<string, bool> { ["badges"] = true, ["tags"] = true, ["marks"] = false }, saved.PackSelection);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
        Assert.True(dashboard.RestoreOriginalCommand.CanExecute(null));
    }

    [Fact]
    public async Task Toggle_MarksARebuildAndApplyRebuildsWithTheOverlay()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);
        byte[] firstPatch = _fixture.ReadIcon();

        dashboard.Packs[2].IsSelected = true;
        await WaitForAsync(() =>
        {
            return dashboard.IconStatus?.Pending == PendingAction.Rebuild;
        });
        Assert.Contains("wybór paczek się zmienił", dashboard.PendingText);
        await dashboard.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Przebudowano zmiany z oryginału (3 ikony).", dashboard.LastAction?.Text);
        Assert.NotEqual(firstPatch, _fixture.ReadIcon());
        Assert.True(_fixture.FileSystem.FileExists(_fixture.Paths.GetCachedAssetPath(TestManifests.OverlayAssetPath)));
    }

    [Fact]
    public async Task Apply_WithNothingSelected_RestoresTheOriginal()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);
        foreach (PackViewModel pack in dashboard.Packs)
        {
            pack.IsSelected = false;
        }

        await WaitForAsync(() =>
        {
            return dashboard.IconStatus?.Pending == PendingAction.Restore;
        });
        await dashboard.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Przywrócono oryginalny Icon.utx.", dashboard.LastAction?.Text);
        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
    }

    [Fact]
    public async Task RestoreOriginal_PutsTheBackupBack()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);

        await dashboard.RestoreOriginalCommand.ExecuteAsync(null);

        Assert.Equal("Przywrócono oryginalny Icon.utx.", dashboard.LastAction?.Text);
        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
        Assert.Equal(PatchFileState.Original, dashboard.IconStatus?.State);
        Assert.Empty(_fixture.Dialogs.Confirmations);
    }

    [Fact]
    public async Task UnknownIconUtx_IsLeftAloneAndRestoreAsksFirst()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);
        byte[] dexUpdate = _fixture.Original.ToArray();
        dexUpdate[^1] ^= 0xFF;
        _fixture.FileSystem.Seed(_fixture.IconPath, dexUpdate);

        await dashboard.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(PatchFileState.Unknown, dashboard.IconStatus?.State);
        Assert.Equal(StatusSeverity.Warning, dashboard.IconState.Severity);
        Assert.Contains("Nieznana wersja Icon.utx", dashboard.IconState.Text);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
        Assert.True(dashboard.RestoreOriginalCommand.CanExecute(null));

        _fixture.Dialogs.ConfirmAnswer = false;
        await dashboard.RestoreOriginalCommand.ExecuteAsync(null);
        Assert.Single(_fixture.Dialogs.Confirmations);
        Assert.Equal(dexUpdate, _fixture.ReadIcon());
        Assert.Contains("Anulowano", dashboard.LastAction?.Text);

        _fixture.Dialogs.ConfirmAnswer = true;
        await dashboard.RestoreOriginalCommand.ExecuteAsync(null);
        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
    }

    [Fact]
    public async Task KillSwitch_RestoresOurPatchOnLoadAndLocksThePacks()
    {
        DashboardViewModel first = await _fixture.CreateDashboardAsync();
        await first.ApplyCommand.ExecuteAsync(null);
        JsonObject manifest = _fixture.Manifest();
        manifest["enabled"] = false;
        manifest["version"] = "2026.10.20.2";
        _fixture.Publish(manifest);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(_fixture.FileSystem.FileExists(_fixture.Paths.SettingsPath) ? null : _fixture.Settings);

        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
        Assert.Contains(dashboard.Banners, banner => banner.Text.Contains("wyłącznik awaryjny"));
        Assert.Equal("Lider CP wyłączył zmiany – przywrócono oryginalny Icon.utx.", dashboard.LastAction?.Text);
        Assert.False(dashboard.PacksEditable);
        Assert.All(dashboard.Packs, pack =>
        {
            Assert.False(pack.IsEditable);
        });
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task KillSwitch_WhileTheGameRuns_WaitsAndSaysWhy()
    {
        DashboardViewModel first = await _fixture.CreateDashboardAsync();
        await first.ApplyCommand.ExecuteAsync(null);
        byte[] patched = _fixture.ReadIcon();
        JsonObject manifest = _fixture.Manifest();
        manifest["enabled"] = false;
        _fixture.Publish(manifest);
        _fixture.Processes.Running.Add("L2.bin");

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(null);

        Assert.Equal(patched, _fixture.ReadIcon());
        Assert.Contains("gra jest uruchomiona", dashboard.LastAction?.Text);
    }

    [Fact]
    public async Task GameRunning_BlocksApplyRestoreAndPresets()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        await dashboard.ApplyCommand.ExecuteAsync(null);
        byte[] patched = _fixture.ReadIcon();
        _fixture.Processes.Running.Add("L2");

        await dashboard.RestoreOriginalCommand.ExecuteAsync(null);
        Assert.Equal(StatusTexts.GameRunning, dashboard.LastAction?.Text);
        await dashboard.ApplyPresetCommand.ExecuteAsync(dashboard.Presets[0]);
        Assert.Equal(StatusTexts.GameRunning, dashboard.LastAction?.Text);
        dashboard.Packs[0].IsSelected = false;
        await WaitForAsync(() =>
        {
            return dashboard.IconStatus?.Pending == PendingAction.Rebuild;
        });
        await dashboard.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(StatusTexts.GameRunning, dashboard.LastAction?.Text);

        Assert.Equal(patched, _fixture.ReadIcon());
        Assert.Equal(LauncherFixture.OriginalOptionIni, _fixture.ReadOptionIni());
    }

    [Fact]
    public async Task Sections_AreExpandedByDefaultAndRememberCollapsing()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        Assert.True(dashboard.PacksSection.IsExpanded);
        Assert.True(dashboard.AnnouncementsSection.IsExpanded);
        Assert.True(dashboard.PresetsSection.IsExpanded);

        dashboard.AnnouncementsSection.IsExpanded = false;
        dashboard.PresetsSection.IsExpanded = false;
        dashboard.PresetsSection.IsExpanded = true;

        var reopened = new DashboardViewModel(_fixture.CreateServices(), () =>
        {
            return Task.CompletedTask;
        }, Array.Empty<string>());
        Assert.True(reopened.PacksSection.IsExpanded);
        Assert.False(reopened.AnnouncementsSection.IsExpanded);
        Assert.True(reopened.PresetsSection.IsExpanded);
    }

    [Fact]
    public async Task Sections_SummarizeTheirContentForTheCollapsedHeader()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal("Wybrano 2 z 3", dashboard.PacksSection.Summary);
        Assert.Equal("Najnowsze: 20.10.2026", dashboard.AnnouncementsSection.Summary);
        Assert.Equal("1 preset", dashboard.PresetsSection.Summary);

        dashboard.Packs[2].IsSelected = true;
        await dashboard.ApplyPresetCommand.ExecuteAsync(dashboard.Presets[0]);

        Assert.Equal("Wybrano 3 z 3", dashboard.PacksSection.Summary);
        Assert.Equal("Ustawiony: Masowe PvP", dashboard.PresetsSection.Summary);
    }

    [Fact]
    public async Task Offline_UsesTheCachedManifestAndSaysSo()
    {
        await _fixture.CreateDashboardAsync();
        _fixture.TakeSourceOffline();

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(null);

        Assert.Equal("zapisana kopia", dashboard.ManifestOriginText);
        NoticeViewModel banner = Assert.Single(dashboard.Banners);
        Assert.Equal(StatusSeverity.Warning, banner.Severity);
        Assert.Contains("Brak dostępu do źródła przepisów", banner.Text);
        Assert.Contains("2026.10.20.1", banner.Text);
        Assert.Equal(3, dashboard.Packs.Count);
        await dashboard.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(PatchFileState.OurPatch, dashboard.IconStatus?.State);
    }

    [Fact]
    public async Task Offline_MissingOverlayImage_DoesNotApplyHalfTheSelection()
    {
        var settings = _fixture.Settings with { PackSelection = new Dictionary<string, bool> { ["marks"] = true } };
        new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Save(settings);
        var services = _fixture.CreateServices();
        await new ManifestService(_fixture.FileSystem, _fixture.Paths, _fixture.TrustedKeys())
            .LoadAsync(new FolderManifestSource(_fixture.FileSystem, LauncherFixture.SourceDirectory), CancellationToken.None);
        _fixture.TakeSourceOffline();
        var dashboard = new DashboardViewModel(services, () =>
        {
            return Task.CompletedTask;
        }, Array.Empty<string>());
        await dashboard.InitializeAsync();

        await dashboard.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(StatusSeverity.Error, dashboard.LastAction?.Severity);
        Assert.Contains("Brakuje obrazków", dashboard.LastAction?.Text);
        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
        Assert.Contains(TestManifests.OverlayAssetPath, dashboard.Details);
    }

    [Fact]
    public async Task NoManifestAtAll_ShowsAnErrorAndAllowsNothingButRestore()
    {
        _fixture.TakeSourceOffline();

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal("brak", dashboard.ManifestVersionText);
        Assert.Equal(StatusSeverity.Error, Assert.Single(dashboard.Banners).Severity);
        Assert.Empty(dashboard.Packs);
        Assert.Contains("Bez przepisów", dashboard.IconState.Text);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
        Assert.False(dashboard.RestoreOriginalCommand.CanExecute(null));
    }

    [Fact]
    public async Task BadSignature_FallsBackToTheCacheWithAWarning()
    {
        await _fixture.CreateDashboardAsync();
        _fixture.Publish(_fixture.Manifest(), L2Mod.Core.Signing.ManifestSignature.GenerateKeyPair().PrivateKeyPem);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(null);

        Assert.Equal("zapisana kopia", dashboard.ManifestOriginText);
        Assert.Contains(dashboard.Banners, banner => banner.Text.Contains("nieprawidłowy podpis"));
    }

    [Fact]
    public async Task NewerLauncherVersion_ShowsTheIndicator()
    {
        JsonObject manifest = _fixture.Manifest();
        manifest["launcher"]!["version"] = "1.2.0";
        _fixture.Publish(manifest);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal("Dostępna nowa wersja launchera: 1.2.0", dashboard.UpdateText);
    }

    [Fact]
    public async Task NewerSchema_KeepsOurPatchAndLocksThePacks()
    {
        DashboardViewModel first = await _fixture.CreateDashboardAsync();
        await first.ApplyCommand.ExecuteAsync(null);
        byte[] patched = _fixture.ReadIcon();
        _fixture.Publish(new JsonObject
        {
            ["schema"] = 2,
            ["enabled"] = true,
            ["launcher"] = new JsonObject { ["version"] = "2.0.0", ["url"] = "launcher/CpLauncher-2.0.0.exe", ["sha256"] = new string('c', 64) },
        });

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(null);

        Assert.Equal(patched, _fixture.ReadIcon());
        Assert.Contains(dashboard.Banners, banner => banner.IsError && banner.Text.Contains("nowszej wersji launchera"));
        Assert.Equal("Dostępna nowa wersja launchera: 2.0.0", dashboard.UpdateText);
        Assert.Equal("Zmiany zostają bez zmian do czasu aktualizacji launchera.", dashboard.PendingText);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SkippedPack_IsReportedAsANote()
    {
        JsonObject manifest = _fixture.Manifest();
        manifest["packs"]![1]!["operations"]![0]!["op"] = "recolor";
        _fixture.Publish(manifest);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal(new[] { "badges", "marks" }, dashboard.Packs.Select(pack => pack.Id));
        Assert.Contains(dashboard.Banners, banner => banner.Text.Contains("pominął część przepisów"));
        Assert.Contains(dashboard.Details, detail => detail.Contains("recolor"));
    }

    [Fact]
    public async Task DevelopmentKey_IsFlagged()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(developmentKey: true);

        Assert.Contains(dashboard.Banners, banner => banner.Text.Contains("kluczem deweloperskim"));
    }

    [Fact]
    public async Task Preset_AppliesAndRestoreMySettingsPutsTheFileBack()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();
        PresetViewModel preset = dashboard.Presets[0];
        Assert.False(preset.IsApplied);
        Assert.False(dashboard.RestoreMySettingsCommand.CanExecute(null));

        await dashboard.ApplyPresetCommand.ExecuteAsync(preset);

        Assert.Equal("Ustawiono „Masowe PvP” (1 opcja w Option.ini).", dashboard.LastAction?.Text);
        Assert.Contains("EffectDetail=0", _fixture.ReadOptionIni());
        Assert.True(preset.IsApplied);
        Assert.True(dashboard.RestoreMySettingsCommand.CanExecute(null));

        await dashboard.RestoreMySettingsCommand.ExecuteAsync(null);

        Assert.Equal("Przywrócono Twoje ustawienia Option.ini.", dashboard.LastAction?.Text);
        Assert.Equal(LauncherFixture.OriginalOptionIni, _fixture.ReadOptionIni());
        Assert.False(preset.IsApplied);
        Assert.False(dashboard.RestoreMySettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Preset_WithoutOptionIni_SaysToStartTheGameOnce()
    {
        _fixture.FileSystem.DeleteFile(_fixture.OptionIniPath);
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        await dashboard.ApplyPresetCommand.ExecuteAsync(dashboard.Presets[0]);

        Assert.Contains("Nie znaleziono system_season\\Option.ini", dashboard.LastAction?.Text);
        Assert.False(_fixture.FileSystem.FileExists(_fixture.OptionIniPath));
    }

    [Fact]
    public async Task LaunchGame_StartsTheConfiguredExecutableInItsFolder()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        dashboard.LaunchGameCommand.Execute(null);

        (string path, string workingDirectory) = Assert.Single(_fixture.Game.Started);
        Assert.Equal(@"C:\client\L2Updater.exe", path);
        Assert.Equal(@"C:\client", workingDirectory);
        Assert.Equal("Uruchomiono L2Updater.exe. Uwaga: wybrane zmiany nie zostały zastosowane.", dashboard.LastAction?.Text);
    }

    [Fact]
    public async Task LaunchGame_ReportsAMissingOrFailingExecutable()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(_fixture.Settings with { GameExecutable = GameExecutables.DexLauncher });

        dashboard.LaunchGameCommand.Execute(null);
        Assert.Contains(@"Nie znaleziono data\L2Launcher.exe", dashboard.LastAction?.Text);

        _fixture.FileSystem.Seed(@"C:\client\data\L2Launcher.exe", new byte[] { 0x4D, 0x5A });
        _fixture.Game.Failure = new Win32Exception(740, "The requested operation requires elevation.");
        dashboard.LaunchGameCommand.Execute(null);
        Assert.Contains("Nie udało się uruchomić", dashboard.LastAction?.Text);
        Assert.Empty(_fixture.Game.Started);
    }

    [Fact]
    public async Task SaveSettings_ValidatesTheSourceAndStoresTheExecutable()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        dashboard.ManifestSourceText = "relative\\folder";
        await dashboard.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("nie jest adresem", dashboard.SettingsMessage);

        dashboard.ManifestSourceText = LauncherFixture.SourceDirectory;
        dashboard.SelectedGameExecutable = dashboard.GameExecutableOptions[1];
        await dashboard.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Zapisano ustawienia.", dashboard.SettingsMessage);
        LauncherSettings saved = new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Load().Settings;
        Assert.Equal(GameExecutables.DexLauncher, saved.GameExecutable);
        Assert.Equal(LauncherFixture.SourceDirectory, saved.ManifestSource);
    }

    [Fact]
    public async Task SaveSettings_NewSource_ReloadsTheManifest()
    {
        JsonObject other = _fixture.Manifest();
        other["version"] = "2026.11.01.1";
        byte[] bytes = TestManifests.Serialize(other);
        _fixture.FileSystem.Seed(@"C:\other\manifest.json", bytes);
        _fixture.FileSystem.Seed(@"C:\other\manifest.json.sig", L2Mod.Core.Signing.ManifestSignature.Sign(bytes, _fixture.Key.PrivateKeyPem));
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        dashboard.ManifestSourceText = @"C:\other";
        await dashboard.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("2026.11.01.1", dashboard.ManifestVersionText);
        Assert.Equal(@"C:\other", dashboard.ManifestSourceDescription);
    }

    [Fact]
    public async Task CustomExecutableFromSettings_IsKeptAsAnOption()
    {
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(_fixture.Settings with { GameExecutable = @"tools\Dex.exe" });

        Assert.Equal(3, dashboard.GameExecutableOptions.Count);
        Assert.Equal(@"tools\Dex.exe", dashboard.SelectedGameExecutable.RelativePath);
    }

    [Fact]
    public async Task DamagedStateJson_IsShownAsAnErrorWithoutTouchingFiles()
    {
        _fixture.FileSystem.Seed(_fixture.InstallerOptions.StatePath, "{ broken"u8.ToArray());

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal(StatusSeverity.Error, dashboard.IconState.Severity);
        Assert.Contains("Nie można sprawdzić Icon.utx", dashboard.IconState.Text);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
        Assert.Equal(_fixture.Original, _fixture.ReadIcon());
    }

    private static bool IsClientPath(string path)
    {
        return path.StartsWith(LauncherFixture.ClientDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pack toggles refresh the status in the background; wait for it (bounded).</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The condition was not met in time.");
    }
}
