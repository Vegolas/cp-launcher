using System.ComponentModel;
using System.Text.Json.Nodes;
using CpLauncher.Services;
using CpLauncher.ViewModels;
using L2Mod.Core.Tests;
using L2Mod.Core.Tests.Updates;
using L2Mod.Core.Updates;

namespace CpLauncher.Tests;

/// <summary>The "Aktualizuj" button and the restarts, on Core's real <see cref="UpdateService"/> over fakes.</summary>
public class LauncherUpdateTests
{
    private const string NewPath = LauncherFixture.LauncherPath + ".new";
    private const string OldPath = LauncherFixture.LauncherPath + ".old";

    private readonly LauncherFixture _fixture = new LauncherFixture();
    private readonly byte[] _current = UpdateTestData.Executable(1);
    private readonly byte[] _release = UpdateTestData.Executable(2);

    public LauncherUpdateTests()
    {
        _fixture.Publish(_fixture.Manifest());
        _fixture.UpdateFiles.Seed(LauncherFixture.LauncherPath, _current);
    }

    private LauncherSettings HttpSettings
    {
        get
        {
            return _fixture.Settings with { ManifestSource = LauncherFixture.HttpSource };
        }
    }

    [Fact]
    public async Task Update_DownloadsVerifiesStartsTheHandOffAndQuits()
    {
        _fixture.PublishRelease(_release);
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(HttpSettings);

        Assert.Equal("Dostępna nowa wersja launchera: 1.0.1", dashboard.UpdateText);
        Assert.Null(dashboard.UpdateHint);
        Assert.True(dashboard.UpdateLauncherCommand.CanExecute(null));

        await dashboard.UpdateLauncherCommand.ExecuteAsync(null);

        StartedProcess handOff = Assert.Single(_fixture.Launchers.Started);
        Assert.Equal(NewPath, handOff.ExecutablePath);
        Assert.Equal(new[] { "--install-update", "1.0.0" }, handOff.Arguments);
        Assert.Equal(_release, _fixture.UpdateFiles.Read(NewPath));
        Assert.Equal(_current, _fixture.UpdateFiles.Read(LauncherFixture.LauncherPath));
        Assert.Equal(new Uri(new Uri(LauncherFixture.HttpSource), "launcher/CpLauncher-1.0.1.exe"), Assert.Single(_fixture.Downloads.Requests));
        Assert.Equal(1, _fixture.Lifetime.Shutdowns);
        Assert.Equal(StatusSeverity.Success, dashboard.LastAction?.Severity);
        Assert.Contains("1.0.1", dashboard.LastAction?.Text);
        Assert.True(dashboard.IsBusy);
        Assert.False(dashboard.ApplyCommand.CanExecute(null));
        Assert.False(dashboard.LaunchGameCommand.CanExecute(null));
        Assert.False(dashboard.UpdateLauncherCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("1.0.1", true)]
    [InlineData("1.1.0", true)]
    [InlineData("2.0.0-beta.1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0-rc.1", false)]
    [InlineData("0.9.9", false)]
    public async Task Update_IsOfferedOnlyForNewerReleases(string version, bool offered)
    {
        _fixture.PublishRelease(_release, version);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(HttpSettings);

        Assert.Equal(offered, dashboard.UpdateText is not null);
        Assert.Equal(offered, dashboard.UpdateLauncherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_WrongHash_IsDiscardedAndCanBeRetried()
    {
        _fixture.PublishRelease(_release);
        _fixture.Downloads.Serve(new Uri(new Uri(LauncherFixture.HttpSource), "launcher/CpLauncher-1.0.1.exe"), UpdateTestData.Executable(3));
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(HttpSettings);

        await dashboard.UpdateLauncherCommand.ExecuteAsync(null);

        Assert.Empty(_fixture.Launchers.Started);
        Assert.Equal(0, _fixture.Lifetime.Shutdowns);
        Assert.Equal(StatusSeverity.Error, dashboard.LastAction?.Severity);
        Assert.Contains("Nie udało się pobrać aktualizacji", dashboard.LastAction?.Text);
        Assert.Contains(dashboard.Details, detail => detail.Contains("SHA-256"));
        Assert.False(_fixture.UpdateFiles.FileExists(NewPath));
        Assert.Equal(_current, _fixture.UpdateFiles.Read(LauncherFixture.LauncherPath));
        Assert.False(dashboard.IsBusy);
        Assert.True(dashboard.UpdateLauncherCommand.CanExecute(null));
        Assert.True(dashboard.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_HandOffBlocked_ReportsAndKeepsRunning()
    {
        _fixture.PublishRelease(_release);
        _fixture.Launchers.Failure = new Win32Exception(225, "Operation did not complete successfully because the file contains a virus.");
        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(HttpSettings);

        await dashboard.UpdateLauncherCommand.ExecuteAsync(null);

        Assert.Equal(0, _fixture.Lifetime.Shutdowns);
        Assert.Contains("nie uruchomiła się", dashboard.LastAction?.Text);
        Assert.Contains(dashboard.Details, detail => detail.Contains("virus"));
        Assert.False(_fixture.UpdateFiles.FileExists(NewPath));
        Assert.False(dashboard.IsBusy);
    }

    [Fact]
    public async Task Update_FromAFolderSource_IsShownButNotDownloaded()
    {
        JsonObject manifest = _fixture.Manifest();
        manifest["launcher"]!["version"] = "1.0.1";
        _fixture.Publish(manifest);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync();

        Assert.Equal("Dostępna nowa wersja launchera: 1.0.1", dashboard.UpdateText);
        Assert.Contains("tylko ze źródła https", dashboard.UpdateHint);
        Assert.False(dashboard.UpdateLauncherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_OverPlainHttpToAnotherComputer_IsNotDownloaded()
    {
        _fixture.PublishRelease(_release);

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(_fixture.Settings with { ManifestSource = "http://cdn.example.test/cp/" });

        Assert.NotNull(dashboard.UpdateText);
        Assert.Contains("tylko ze źródła https", dashboard.UpdateHint);
        Assert.False(dashboard.UpdateLauncherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_FromTheCachedManifest_AsksToGoOnline()
    {
        _fixture.PublishRelease(_release);
        await _fixture.CreateDashboardAsync(HttpSettings);
        _fixture.Http.Responses.Clear();

        DashboardViewModel dashboard = await _fixture.CreateDashboardAsync(HttpSettings);

        Assert.Equal("zapisana kopia", dashboard.ManifestOriginText);
        Assert.NotNull(dashboard.UpdateText);
        Assert.Contains("Połącz się z internetem", dashboard.UpdateHint);
        Assert.False(dashboard.UpdateLauncherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_SkippedVersionIsNotOfferedButANewerOneIs()
    {
        _fixture.PublishRelease(_release);
        LauncherSettings skipping = HttpSettings with { SkippedLauncherVersion = "1.0.1" };

        DashboardViewModel skipped = await _fixture.CreateDashboardAsync(skipping);
        _fixture.PublishRelease(UpdateTestData.Executable(4), "1.0.2");
        DashboardViewModel newer = await _fixture.CreateDashboardAsync(skipping);

        Assert.Null(skipped.UpdateText);
        Assert.Contains(skipped.Details, detail => detail.Contains("1.0.1") && detail.Contains("pominięta"));
        Assert.Equal("Dostępna nowa wersja launchera: 1.0.2", newer.UpdateText);
        Assert.True(newer.UpdateLauncherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Update_WaitsForTheStartupCleanupBeforeDownloading()
    {
        _fixture.PublishRelease(_release);
        _fixture.UpdateFiles.Seed(NewPath, new byte[] { 1 });
        LauncherServices services = _fixture.CreateServices(HttpSettings);
        Task<StartupCleanupResult?> cleanup = services.Updates.StartCleanup(LauncherLog.None);
        var dashboard = new DashboardViewModel(services, () =>
        {
            return Task.CompletedTask;
        }, Array.Empty<string>());
        await dashboard.InitializeAsync();

        await dashboard.UpdateLauncherCommand.ExecuteAsync(null);

        Assert.Equal(CleanupOutcome.Deleted, (await cleanup)?.NewVersion);
        Assert.Equal(_release, _fixture.UpdateFiles.Read(NewPath));
        Assert.Single(_fixture.Launchers.Started);
    }

    [Fact]
    public async Task Restart_AfterAnUpdate_IsShownOnTheFirstDashboardOnly()
    {
        UpdateStartupInfo startup = UpdateStartupInfo.Parse(new[] { "--updated-from", "0.9.0" });
        var main = new MainWindowViewModel(_fixture.CreateServices(_fixture.Settings, startup: startup), Array.Empty<string>());

        await main.StartAsync();
        DashboardViewModel first = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        await first.ChangeClientFolderCommand.ExecuteAsync(null);
        await ((SetupViewModel)main.CurrentPage!).CancelCommand.ExecuteAsync(null);
        DashboardViewModel second = Assert.IsType<DashboardViewModel>(main.CurrentPage);

        NoticeViewModel notice = Assert.Single(first.Banners);
        Assert.Equal(StatusSeverity.Success, notice.Severity);
        Assert.Equal("Zaktualizowano launcher z wersji 0.9.0 do 1.0.0.", notice.Text);
        Assert.Empty(second.Banners);
    }

    [Theory]
    [InlineData("--update-failed", "Nie udało się zainstalować wersji 1.0.1")]
    [InlineData("--rolled-back-from", "Wersja 1.0.1 launchera nie uruchomiła się poprawnie")]
    public async Task Restart_AfterAFailedOrRolledBackVersion_SkipsItFromNowOn(string option, string banner)
    {
        _fixture.PublishRelease(_release);
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--data", LauncherFixture.DataDirectory, "--client", LauncherFixture.ClientDirectory, "--source", LauncherFixture.HttpSource });
        LauncherHost host = _fixture.CreateHost(UpdateStartupInfo.Parse(new[] { option, "1.0.1" }));

        LauncherStartup startup = LauncherBootstrap.Create(options, _fixture.FileSystem, _fixture.Processes, _fixture.Game, _fixture.Dialogs, null, host);
        var dashboard = new DashboardViewModel(startup.Services, () =>
        {
            return Task.CompletedTask;
        }, startup.Notes);
        await dashboard.InitializeAsync();

        Assert.Equal("1.0.1", new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Load().Settings.SkippedLauncherVersion);
        Assert.Contains(dashboard.Banners, notice => notice.IsWarning && notice.Text.StartsWith(banner, StringComparison.Ordinal));
        Assert.Null(dashboard.UpdateText);
    }

    [Fact]
    public async Task StartupCleanup_RemovesTheSideFiles()
    {
        _fixture.UpdateFiles.Seed(OldPath, _current);
        _fixture.UpdateFiles.Seed(NewPath, _release);
        LauncherUpdates updates = _fixture.CreateUpdates(UpdateStartupInfo.Parse(new[] { "--updated-from", "0.9.0" }));

        StartupCleanupResult? result = await updates.StartCleanup(LauncherLog.None);

        Assert.Equal((CleanupOutcome.Deleted, CleanupOutcome.Deleted), (result?.PreviousVersion, result?.NewVersion));
        Assert.False(_fixture.UpdateFiles.FileExists(OldPath));
        Assert.Equal(_current, _fixture.UpdateFiles.Read(LauncherFixture.LauncherPath));
    }

    [Fact]
    public void HandOff_InstallsItselfAndStartsTheNewLauncher()
    {
        _fixture.UpdateFiles.Seed(NewPath, _release);
        LauncherUpdates updates = _fixture.CreateUpdates(UpdateStartupInfo.Parse(new[] { "--install-update", "0.9.0" }));
        var shown = new List<string>();

        int exitCode = updates.RunHandOff(LauncherLog.None, shown.Add);

        Assert.Equal(0, exitCode);
        Assert.Equal(_release, _fixture.UpdateFiles.Read(LauncherFixture.LauncherPath));
        Assert.Equal(_current, _fixture.UpdateFiles.Read(OldPath));
        StartedProcess started = Assert.Single(_fixture.Launchers.Started);
        Assert.Equal((LauncherFixture.LauncherPath, "--updated-from 0.9.0"), (started.ExecutablePath, string.Join(' ', started.Arguments)));
        Assert.Empty(shown);
    }

    [Fact]
    public void HandOff_WithoutAnyLauncherLeft_TellsTheMemberWhatToRename()
    {
        _fixture.UpdateFiles.Seed(NewPath, _release);
        _fixture.UpdateFiles.FailMove(LauncherFixture.LauncherPath + ".tmp", LauncherFixture.LauncherPath);
        _fixture.UpdateFiles.FailMove(OldPath, LauncherFixture.LauncherPath);
        LauncherUpdates updates = _fixture.CreateUpdates(UpdateStartupInfo.Parse(new[] { "--install-update", "0.9.0" }));
        var shown = new List<string>();

        int exitCode = updates.RunHandOff(LauncherLog.None, shown.Add);

        Assert.Equal(1, exitCode);
        string text = Assert.Single(shown);
        Assert.StartsWith("Aktualizacja launchera nie powiodła się", text);
        Assert.Contains("CpLauncher.exe.old", text);
    }

    [Fact]
    public void HandOff_ThatCannotInstall_RestartsThePreviousLauncherQuietly()
    {
        LauncherUpdates updates = _fixture.CreateUpdates(UpdateStartupInfo.Parse(new[] { "--install-update", "0.9.0" }));
        var shown = new List<string>();

        int exitCode = updates.RunHandOff(LauncherLog.None, shown.Add);

        Assert.Equal(1, exitCode);
        Assert.Empty(shown);
        StartedProcess started = Assert.Single(_fixture.Launchers.Started);
        Assert.Equal((LauncherFixture.LauncherPath, "--update-failed 1.0.0"), (started.ExecutablePath, string.Join(' ', started.Arguments)));
        Assert.Equal(_current, _fixture.UpdateFiles.Read(LauncherFixture.LauncherPath));
    }

    [Fact]
    public void CrashOfAFreshUpdate_RollsBackUntilTheWindowWorked()
    {
        _fixture.UpdateFiles.Seed(OldPath, _current);
        LauncherUpdates updated = _fixture.CreateUpdates(UpdateStartupInfo.Parse(new[] { "--updated-from", "0.9.0" }));

        Assert.False(updated.TryRollBackAfterCrash(startupCompleted: true, LauncherLog.None));
        Assert.False(_fixture.CreateUpdates().TryRollBackAfterCrash(startupCompleted: false, LauncherLog.None));
        Assert.Empty(_fixture.Launchers.Started);
        Assert.True(updated.TryRollBackAfterCrash(startupCompleted: false, LauncherLog.None));

        StartedProcess started = Assert.Single(_fixture.Launchers.Started);
        Assert.Equal((OldPath, "--install-rollback 1.0.0"), (started.ExecutablePath, string.Join(' ', started.Arguments)));
        _fixture.UpdateFiles.Delete(OldPath);
        Assert.False(updated.TryRollBackAfterCrash(startupCompleted: false, LauncherLog.None));
    }

    [Fact]
    public void ReleaseBase_IsTheHttpsSourceOrLoopbackHttp()
    {
        var http = new HttpClient(new StubHttpHandler());

        Assert.Equal(new Uri("https://cdn.example.test/cp/"), LauncherUpdates.GetReleaseBase(new HttpManifestSource(http, new Uri("https://cdn.example.test/cp"))));
        Assert.Equal(new Uri("http://127.0.0.1:8123/"), LauncherUpdates.GetReleaseBase(new HttpManifestSource(http, new Uri("http://127.0.0.1:8123/"))));
        Assert.Null(LauncherUpdates.GetReleaseBase(new HttpManifestSource(http, new Uri("http://cdn.example.test/cp/"))));
        Assert.Null(LauncherUpdates.GetReleaseBase(new FolderManifestSource(new InMemoryFileSystem(), LauncherFixture.SourceDirectory)));
        Assert.Null(LauncherUpdates.GetReleaseBase(null));
    }
}
