using System.Net;
using L2Mod.Core.Manifests;
using L2Mod.Core.Updates;
using static L2Mod.Core.Tests.Updates.UpdateTestData;

namespace L2Mod.Core.Tests.Updates;

public class UpdateServiceTests
{
    private const string ExePath = @"C:\Games\CpLauncher\CpLauncher.exe";
    private const string NewPath = ExePath + ".new";
    private const string OldPath = ExePath + ".old";
    private const string StagingPath = ExePath + ".tmp";

    private readonly InMemoryUpdateFileSystem _fileSystem = new InMemoryUpdateFileSystem();
    private readonly FakeDownloadClient _downloads = new FakeDownloadClient();
    private readonly RecordingProcessStarter _processes = new RecordingProcessStarter();
    private readonly RecordingDelay _delay = new RecordingDelay();
    private readonly FakeInstanceLock _instanceLock = new FakeInstanceLock();
    private readonly byte[] _current = Executable(1);
    private readonly byte[] _release = Executable(2);

    public UpdateServiceTests()
    {
        _fileSystem.Seed(ExePath, _current);
        _downloads.Serve(ReleaseUri, _release);
    }

    [Fact]
    public void Check_NewerRelease_IsAvailable()
    {
        UpdateCheckResult result = CreateService().Check(Manifest(Release(_release)));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(V101, result.Available);
    }

    [Fact]
    public void Check_SameVersion_IsUpToDate()
    {
        UpdateCheckResult result = CreateService(V101).Check(Manifest(Release(_release)));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void Check_OlderRelease_IsNeverADowngrade()
    {
        UpdateCheckResult result = CreateService(LauncherVersion.Parse("1.1.0")).Check(Manifest(Release(_release)));

        Assert.Equal(UpdateCheckStatus.CurrentIsNewer, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void Check_PreReleaseBuild_GetsTheRelease()
    {
        UpdateCheckResult result = CreateService(LauncherVersion.Parse("1.0.1-beta.2+abc123")).Check(Manifest(Release(_release)));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public void Check_IgnoresTheKillSwitch()
    {
        UpdateCheckResult result = CreateService().Check(Manifest(Release(_release), enabled: false));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public void Check_WithoutLauncherEntry_IsNoRelease()
    {
        Assert.Equal(UpdateCheckStatus.NoRelease, CreateService().Check(Manifest(null)).Status);
    }

    [Fact]
    public void Check_RolledBackVersion_IsSkipped()
    {
        UpdateCheckResult result = CreateService().Check(Manifest(Release(_release)), skippedVersion: V101);

        Assert.Equal(UpdateCheckStatus.Skipped, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Theory]
    [InlineData("01.0.1", ReleaseUrl)]
    [InlineData("1.0.1", "https://evil.example.invalid/CpLauncher.exe")]
    [InlineData("1.0.1", "../other/CpLauncher.exe")]
    [InlineData("1.0.1", "//evil.example.invalid/CpLauncher.exe")]
    public void Check_InvalidRelease_IsIgnored(string version, string url)
    {
        UpdateCheckResult result = CreateService().Check(Manifest(Release(_release, version, url)));

        Assert.Equal(UpdateCheckStatus.InvalidRelease, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void Check_BadHash_IsIgnored()
    {
        var release = new LauncherRelease("1.0.1", ReleaseUrl, "not-a-hash");

        Assert.Equal(UpdateCheckStatus.InvalidRelease, CreateService().Check(Manifest(release)).Status);
    }

    [Fact]
    public void Constructor_RejectsPlainHttpAndRelativeExePath()
    {
        var http = new UpdateOptions(ExePath, V100, new Uri("http://cdn.example.invalid/cp/"));
        var relative = new UpdateOptions("CpLauncher.exe", V100, BaseUri);

        Assert.Throws<ArgumentException>(() => CreateService(http));
        Assert.Throws<ArgumentException>(() => CreateService(relative));
    }

    [Fact]
    public async Task Download_ResolvesTheUrlAgainstABaseWithoutTrailingSlash()
    {
        UpdateService service = CreateService(new UpdateOptions(ExePath, V100, new Uri("https://cdn.example.invalid/cp")));

        await service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))));

        Assert.Equal(ReleaseUri, Assert.Single(_downloads.Requests));
    }

    [Fact]
    public async Task Download_WritesTheVerifiedNewFileAndLeavesTheExeAlone()
    {
        UpdateService service = CreateService();
        var progress = new CollectingProgress();

        DownloadedUpdate update = await service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))), progress);

        Assert.Equal((NewPath, V101, (long)_release.Length), (update.Path, update.Version, update.Length));
        Assert.Equal(ContentHash.Sha256Hex(_release), update.Sha256);
        Assert.Equal(_release, _fileSystem.Read(NewPath));
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.DoesNotContain(_fileSystem.Operations, operation => operation.Contains(ExePath + " ") || operation.EndsWith(ExePath));
        Assert.Equal(new DownloadProgress(_release.Length, _release.Length), progress.Reports[^1]);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task Download_HashMismatch_DeletesTheFile()
    {
        UpdateService service = CreateService();
        byte[] tampered = (byte[])_release.Clone();
        tampered[1000] ^= 0xFF;
        _downloads.Serve(ReleaseUri, tampered);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Contains("SHA-256", error.Message);
        Assert.False(_fileSystem.FileExists(NewPath));
        Assert.Equal(_current, _fileSystem.Read(ExePath));
    }

    [Fact]
    public async Task Download_AnnouncedSizeOverTheLimit_FailsBeforeWriting()
    {
        UpdateService service = CreateService(Options(ExePath, V100) with { MaxDownloadBytes = _release.Length - 1 });

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.DoesNotContain(_fileSystem.Operations, operation => operation.StartsWith("create"));
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_BodyOverTheLimitWithoutLength_DeletesThePartialFile()
    {
        _downloads.Serve(ReleaseUri, _release, announceLength: false);
        UpdateService service = CreateService(Options(ExePath, V100) with { MaxDownloadBytes = 100_000 });

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Contains("limit", error.Message);
        Assert.Contains($"create {NewPath}", _fileSystem.Operations);
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_InterruptedConnection_DeletesThePartialFile()
    {
        _downloads.ServeStream(ReleaseUri, () => ScriptedStream.FailingAfter(_release, 150_000), _release.Length);
        UpdateService service = CreateService();
        var progress = new CollectingProgress();

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))), progress));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(150_000, progress.Reports[^1].BytesReceived);
        Assert.False(_fileSystem.FileExists(NewPath));
        Assert.Equal(_current, _fileSystem.Read(ExePath));
    }

    [Fact]
    public async Task Download_EndingEarly_IsReportedAsTruncated()
    {
        _downloads.ServeStream(ReleaseUri, () => new MemoryStream(_release, 0, 1000), _release.Length);
        UpdateService service = CreateService();

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Contains($"1000 of {_release.Length}", error.Message);
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_StalledConnection_TimesOutAndDeletesThePartialFile()
    {
        _downloads.ServeStream(ReleaseUri, () => ScriptedStream.StallingAfter(_release, 10_000), _release.Length);
        UpdateService service = CreateService(Options(ExePath, V100) with { DownloadStallTimeout = TimeSpan.FromMilliseconds(100) });

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Contains("stalled", error.Message);
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_Cancelled_DeletesThePartialFile()
    {
        _downloads.ServeStream(ReleaseUri, () => ScriptedStream.StallingAfter(_release, 10_000), _release.Length);
        UpdateService service = CreateService();
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))), progress, cancellation.Token));

        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_HttpError_IsAnUpdateException()
    {
        var release = Release(_release, url: "launcher/missing.exe");
        UpdateService service = CreateService();

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(release))));

        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<HttpRequestException>(error.InnerException).StatusCode);
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Download_ReplacesAStaleNewFile()
    {
        _fileSystem.Seed(NewPath, new byte[] { 1, 2, 3 });
        UpdateService service = CreateService();

        await service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))));

        Assert.Equal(_release, _fileSystem.Read(NewPath));
        Assert.Equal($"delete {NewPath}", _fileSystem.Operations[0]);
    }

    [Fact]
    public async Task Download_StaleNewFileInUse_FailsWithoutDownloading()
    {
        _fileSystem.Seed(NewPath, new byte[] { 1, 2, 3 });
        _fileSystem.Lock(NewPath);
        UpdateService service = CreateService();

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Empty(_downloads.Requests);
        Assert.Equal(Options(ExePath, V100).SwapRetry.Attempts - 1, _delay.Waits.Count);
    }

    [Fact]
    public async Task Download_WithoutAnAvailableUpdate_IsRefused()
    {
        UpdateService service = CreateService(V101);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release)))));

        Assert.Empty(_downloads.Requests);
    }

    [Fact]
    public async Task Apply_StartsTheDownloadAsHandOffAndChangesNothingElse()
    {
        UpdateService service = CreateService();
        DownloadedUpdate update = await DownloadAsync(service);

        await service.ApplyAndRestartAsync(update);

        StartedProcess started = Assert.Single(_processes.Started);
        Assert.Equal(NewPath, started.ExecutablePath);
        Assert.Equal(new[] { "--install-update", "1.0.0" }, started.Arguments);
        Assert.Equal(@"C:\Games\CpLauncher", started.WorkingDirectory);
        Assert.Empty(_fileSystem.Operations);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.Equal(_release, _fileSystem.Read(NewPath));
    }

    [Fact]
    public async Task Apply_NewFileChangedSinceTheDownload_DeletesItAndStartsNothing()
    {
        UpdateService service = CreateService();
        DownloadedUpdate update = await DownloadAsync(service);
        _fileSystem.Delete(NewPath);
        _fileSystem.Seed(NewPath, Executable(3));

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.ApplyAndRestartAsync(update));

        Assert.Contains("changed on disk", error.Message);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(NewPath));
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task Apply_HandOffDoesNotStart_DeletesTheDownload()
    {
        UpdateService service = CreateService();
        DownloadedUpdate update = await DownloadAsync(service);
        _processes.Failure = new System.ComponentModel.Win32Exception(225, "Operation did not complete successfully because the file contains a virus.");

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => service.ApplyAndRestartAsync(update));

        Assert.Contains("did not start", error.Message);
        Assert.False(error.RequiresManualRecovery);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Install_SwapsItselfInAndStartsTheNewLauncher()
    {
        SeedHandOff();
        byte[]? exeAtStart = null;
        _processes.OnStart = path => exeAtStart = _fileSystem.Read(path);

        await CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100);

        Assert.Equal(_release, _fileSystem.Read(ExePath));
        Assert.Equal(_current, _fileSystem.Read(OldPath));
        Assert.Equal(_release, _fileSystem.Read(NewPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        Assert.Equal(_release, exeAtStart);
        StartedProcess started = Assert.Single(_processes.Started);
        Assert.Equal(ExePath, started.ExecutablePath);
        Assert.Equal(new[] { "--updated-from", "1.0.0" }, started.Arguments);
        Assert.Equal(new[] { $"create {StagingPath}", $"move {ExePath} -> {OldPath}", $"move {StagingPath} -> {ExePath}" }, _fileSystem.Operations);
        Assert.Empty(_delay.Waits);
    }

    [Fact]
    public async Task ForwardedArguments_FollowEveryStartedLauncher()
    {
        string[] forwarded = { "--data", @"D:\portable\data" };
        UpdateService old = CreateService(Options(ExePath, V100) with { ForwardedArguments = forwarded });
        await old.ApplyAndRestartAsync(await DownloadAsync(old));
        SeedHandOff();

        await CreateService(Options(ExePath, V101) with { ForwardedArguments = forwarded }).InstallAndRestartAsync(InstallUpdateOver100);

        Assert.Equal(
            new[]
            {
                new[] { "--install-update", "1.0.0", "--data", @"D:\portable\data" },
                new[] { "--updated-from", "1.0.0", "--data", @"D:\portable\data" },
            },
            _processes.Started.Select(started => started.Arguments.ToArray()));
    }

    [Fact]
    public void ForwardedArguments_MustNotCarryUpdateOptions()
    {
        UpdateOptions options = Options(ExePath, V100) with { ForwardedArguments = new[] { "--data", @"D:\d", "--updated-from", "0.9.0" } };

        Assert.Throws<ArgumentException>(() => CreateService(options));
        Assert.True(UpdateStartupInfo.ContainsUpdateOption(new[] { "--install-rollback" }));
        Assert.False(UpdateStartupInfo.ContainsUpdateOption(new[] { "--data", "--INSTALL-UPDATE" }));
    }

    [Fact]
    public async Task Install_WaitsUntilThePreviousLauncherHasExited()
    {
        SeedHandOff();
        _fileSystem.MarkInUse(ExePath, checks: 3);

        await CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100);

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500) }, _delay.Waits);
        Assert.Equal(_release, _fileSystem.Read(ExePath));
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task Install_PreviousLauncherNeverExits_ChangesAndStartsNothing()
    {
        SeedHandOff();
        _fileSystem.MarkInUse(ExePath);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.Contains("still running", error.Message);
        Assert.Empty(_fileSystem.Operations);
        Assert.Empty(_processes.Started);
        Assert.Equal(Options(ExePath, V101).PreviousExitWait.Attempts - 1, _delay.Waits.Count);
    }

    [Fact]
    public async Task Install_RemovesAStaleOldFileFirst()
    {
        SeedHandOff();
        _fileSystem.Seed(OldPath, new byte[] { 9 });

        await CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100);

        Assert.Equal(_current, _fileSystem.Read(OldPath));
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task Install_StaleOldFileInUse_RestartsThePreviousLauncher()
    {
        SeedHandOff();
        _fileSystem.Seed(OldPath, new byte[] { 9 });
        _fileSystem.Lock(OldPath, runningExecutable: true);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.False(error.RequiresManualRecovery);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        Assert.Empty(SwapOperations());
        AssertPreviousRestartedAfterFailure();
    }

    [Fact]
    public async Task Install_ExeCannotBeParked_RestartsThePreviousLauncher()
    {
        SeedHandOff();
        _fileSystem.FailMove(ExePath, OldPath);

        await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(OldPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        AssertPreviousRestartedAfterFailure();
    }

    [Fact]
    public async Task Install_CopyCannotBeMovedIn_RollsBackAndRestartsThePreviousLauncher()
    {
        SeedHandOff();
        _fileSystem.FailMove(StagingPath, ExePath);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.False(error.RequiresManualRecovery);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(OldPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        Assert.Equal(new[] { $"move {ExePath} -> {OldPath}", $"move {OldPath} -> {ExePath}" }, SwapOperations());
        Assert.Equal(Options(ExePath, V101).SwapRetry.Attempts - 1, _delay.Waits.Count);
        AssertPreviousRestartedAfterFailure();
    }

    [Fact]
    public async Task Install_CopyBrieflyHeldByAScanner_IsRetried()
    {
        SeedHandOff();
        _fileSystem.FailMove(StagingPath, ExePath, failures: 2);

        await CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100);

        Assert.Equal(_release, _fileSystem.Read(ExePath));
        Assert.Equal(2, _delay.Waits.Count);
        Assert.Equal(new[] { "--updated-from", "1.0.0" }, Assert.Single(_processes.Started).Arguments);
    }

    [Fact]
    public async Task Install_NewLauncherDoesNotStart_RollsBackAndRestartsThePreviousLauncher()
    {
        SeedHandOff();
        _processes.Failure = new System.ComponentModel.Win32Exception(225, "Operation did not complete successfully because the file contains a virus.");
        _processes.FailuresLeft = 1;

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.False(error.RequiresManualRecovery);
        Assert.Contains("previous launcher was started again", error.Message);
        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.False(_fileSystem.FileExists(OldPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        AssertPreviousRestartedAfterFailure();
    }

    [Fact]
    public async Task Install_RollbackFailingToo_AsksForManualRecoveryAndKeepsTheOldFile()
    {
        SeedHandOff();
        _fileSystem.FailMove(StagingPath, ExePath);
        _fileSystem.FailMove(OldPath, ExePath);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => CreateService(V101).InstallAndRestartAsync(InstallUpdateOver100));

        Assert.True(error.RequiresManualRecovery);
        Assert.Contains(OldPath, error.Message);
        Assert.Equal(_current, _fileSystem.Read(OldPath));
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task Install_WithoutHandOffArguments_IsRefused()
    {
        SeedHandOff();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(V101).InstallAndRestartAsync(UpdateStartupInfo.Parse(new[] { "--updated-from", "1.0.0" })));

        Assert.Empty(_fileSystem.Operations);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public void Rollback_StartsThePreviousVersionAsHandOff()
    {
        _fileSystem.Seed(ExePath, _release);
        _fileSystem.Seed(OldPath, _current);
        UpdateService service = CreateService(V101);
        Assert.True(service.CanRollback);

        service.RollbackAndRestart();

        StartedProcess started = Assert.Single(_processes.Started);
        Assert.Equal(OldPath, started.ExecutablePath);
        Assert.Equal(new[] { "--install-rollback", "1.0.1" }, started.Arguments);
        Assert.Empty(_fileSystem.Operations);
    }

    [Fact]
    public async Task Rollback_HandOffPutsThePreviousVersionBack()
    {
        _fileSystem.Seed(ExePath, _release);
        _fileSystem.Seed(OldPath, _current);

        await CreateService(V100).InstallAndRestartAsync(InstallRollbackOver101);

        Assert.Equal(_current, _fileSystem.Read(ExePath));
        Assert.Equal(_release, _fileSystem.Read(NewPath));
        Assert.Equal(_current, _fileSystem.Read(OldPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        StartedProcess started = Assert.Single(_processes.Started);
        Assert.Equal(new[] { "--rolled-back-from", "1.0.1" }, started.Arguments);

        // The restarted previous version skips the release it came back from and removes both side files.
        UpdateStartupInfo startup = UpdateStartupInfo.Parse(started.Arguments);
        UpdateService previous = CreateService(V100);
        Assert.Equal(UpdateCheckStatus.Skipped, previous.Check(Manifest(Release(_release)), startup.RolledBackFrom).Status);
        StartupCleanupResult cleanup = await previous.CleanupOnStartupAsync();
        Assert.Equal((CleanupOutcome.Deleted, CleanupOutcome.Deleted), (cleanup.PreviousVersion, cleanup.NewVersion));
        Assert.Equal(_current, _fileSystem.Read(ExePath));
    }

    [Fact]
    public void Rollback_WithoutPreviousVersion_IsRefused()
    {
        UpdateService service = CreateService(V101);
        Assert.False(service.CanRollback);

        Assert.Throws<UpdateException>(() => service.RollbackAndRestart());

        Assert.Empty(_processes.Started);
    }

    [Fact]
    public void Rollback_HandOffDoesNotStart_ChangesNothing()
    {
        _fileSystem.Seed(ExePath, _release);
        _fileSystem.Seed(OldPath, _current);
        _processes.Failure = new InvalidOperationException("blocked");

        Assert.Throws<UpdateException>(() => CreateService(V101).RollbackAndRestart());

        Assert.Equal(_release, _fileSystem.Read(ExePath));
        Assert.Equal(_current, _fileSystem.Read(OldPath));
        Assert.Empty(_fileSystem.Operations);
    }

    [Fact]
    public async Task Cleanup_DeletesAllSideFiles()
    {
        _fileSystem.Seed(OldPath, _current);
        _fileSystem.Seed(NewPath, new byte[] { 1, 2 });
        _fileSystem.Seed(StagingPath, new byte[] { 3 });

        StartupCleanupResult result = await CreateService(V101).CleanupOnStartupAsync();

        Assert.Equal((CleanupOutcome.Deleted, CleanupOutcome.Deleted, CleanupOutcome.Deleted), (result.PreviousVersion, result.NewVersion, result.StagingCopy));
        Assert.Equal(3, result.Messages.Count);
        Assert.False(_fileSystem.FileExists(OldPath));
        Assert.False(_fileSystem.FileExists(NewPath));
        Assert.False(_fileSystem.FileExists(StagingPath));
        Assert.True(_fileSystem.FileExists(ExePath));
    }

    [Fact]
    public async Task Cleanup_HandOffStillExiting_IsRetriedUntilDeleted()
    {
        _fileSystem.Seed(NewPath, _release);
        _fileSystem.Lock(NewPath, failures: 3, runningExecutable: true);

        StartupCleanupResult result = await CreateService(V101).CleanupOnStartupAsync();

        Assert.Equal(CleanupOutcome.Deleted, result.NewVersion);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500) }, _delay.Waits);
        Assert.False(_fileSystem.FileExists(NewPath));
    }

    [Fact]
    public async Task Cleanup_OldVersionLockedForGood_StaysForTheNextStart()
    {
        _fileSystem.Seed(OldPath, _current);
        _fileSystem.Lock(OldPath, runningExecutable: true);
        RetryPolicy retry = Options(ExePath, V101).CleanupRetry;

        StartupCleanupResult result = await CreateService(V101).CleanupOnStartupAsync();

        Assert.Equal((CleanupOutcome.StillLocked, CleanupOutcome.NotPresent), (result.PreviousVersion, result.NewVersion));
        Assert.Contains("next start", Assert.Single(result.Messages));
        Assert.Equal(retry.Attempts - 1, _delay.Waits.Count);
        Assert.True(_fileSystem.FileExists(OldPath));
    }

    [Fact]
    public async Task Cleanup_WithNothingLeft_DoesNothing()
    {
        StartupCleanupResult result = await CreateService().CleanupOnStartupAsync();

        Assert.Equal((CleanupOutcome.NotPresent, CleanupOutcome.NotPresent, CleanupOutcome.NotPresent), (result.PreviousVersion, result.NewVersion, result.StagingCopy));
        Assert.Empty(result.Messages);
        Assert.Empty(_fileSystem.Operations);
    }

    [Fact]
    public async Task SecondInstance_WithoutTheLock_CannotTouchAnything()
    {
        SeedHandOff();
        _fileSystem.Seed(OldPath, _current);
        UpdateService service = CreateService();
        UpdateCheckResult check = service.Check(Manifest(Release(_release)));
        _instanceLock.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupOnStartupAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndVerifyAsync(check));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAndRestartAsync(InstallUpdateOver100));
        Assert.Throws<InvalidOperationException>(() => service.RollbackAndRestart());

        Assert.Empty(_fileSystem.Operations);
        Assert.Empty(_downloads.Requests);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task OneOperationAtATime()
    {
        _downloads.ServeStream(ReleaseUri, () => ScriptedStream.StallingAfter(_release, 10_000), _release.Length);
        UpdateService service = CreateService();
        using var cancellation = new CancellationTokenSource();
        var reachedStall = new TaskCompletionSource();
        var progress = new CallbackProgress(report =>
        {
            if (report.BytesReceived == 10_000)
            {
                reachedStall.TrySetResult();
            }
        });

        Task download = service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))), progress, cancellation.Token);
        await reachedStall.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupOnStartupAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(CleanupOutcome.NotPresent, (await service.CleanupOnStartupAsync()).NewVersion);
    }

    /// <summary>The hand-off situation: the old launcher at the exe path, the verified new one as .new.</summary>
    private void SeedHandOff()
    {
        _fileSystem.Seed(NewPath, _release);
    }

    private void AssertPreviousRestartedAfterFailure()
    {
        StartedProcess started = Assert.Single(_processes.Started);
        Assert.Equal(ExePath, started.ExecutablePath);
        Assert.Equal(new[] { "--update-failed", "1.0.1" }, started.Arguments);
    }

    private UpdateService CreateService(LauncherVersion? current = null)
    {
        return CreateService(Options(ExePath, current ?? V100));
    }

    private UpdateService CreateService(UpdateOptions options)
    {
        return new UpdateService(options, _instanceLock, _fileSystem, _downloads, _processes, _delay);
    }

    private async Task<DownloadedUpdate> DownloadAsync(UpdateService service)
    {
        DownloadedUpdate update = await service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_release))));
        _fileSystem.Operations.Clear();
        _delay.Waits.Clear();
        return update;
    }

    private List<string> SwapOperations()
    {
        return _fileSystem.Operations.Where(operation => operation.StartsWith("move")).ToList();
    }

    private sealed class CallbackProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _callback;

        public CallbackProgress(Action<DownloadProgress> callback)
        {
            _callback = callback;
        }

        public void Report(DownloadProgress value)
        {
            _callback(value);
        }
    }
}
