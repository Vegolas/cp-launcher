using L2Mod.Core.Updates;
using static L2Mod.Core.Tests.Updates.UpdateTestData;

namespace L2Mod.Core.Tests.Updates;

/// <summary>
/// <see cref="UpdateService"/> on real files in a temporary folder with dummy exe files. Downloads come from
/// <see cref="FakeDownloadClient"/>; processes are only recorded and each "process" is a service instance played
/// in turn. A running or exiting exe is imitated by an open handle that allows reading only, so every rename or
/// delete of it fails: the flow must wait instead of touching it.
/// </summary>
public sealed class UpdateIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "L2Mod.Tests", "update-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDownloadClient _downloads = new FakeDownloadClient();
    private readonly RecordingProcessStarter _processes = new RecordingProcessStarter();
    private readonly RecordingDelay _delay = new RecordingDelay();
    private readonly FakeInstanceLock _instanceLock = new FakeInstanceLock();
    private readonly byte[] _v100 = Executable(10);
    private readonly byte[] _v101 = Executable(11);

    public UpdateIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(ExePath, _v100);
        _downloads.Serve(ReleaseUri, _v101);
    }

    private string ExePath
    {
        get
        {
            return Path.Combine(_directory, "CpLauncher.exe");
        }
    }

    private string NewPath
    {
        get
        {
            return ExePath + ".new";
        }
    }

    private string OldPath
    {
        get
        {
            return ExePath + ".old";
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A leftover in %TEMP% is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [Fact]
    public async Task UpdateCycle_OldLauncherHandOffNewLauncher()
    {
        // 1. The running 1.0.0 downloads, verifies and starts the hand-off; its own exe stays untouched.
        StartedProcess handOffStart;
        using (FileStream runningOld = OpenLikeARunningExe(ExePath))
        {
            UpdateService old = CreateService(ExePath, V100);
            DownloadedUpdate update = await old.DownloadAndVerifyAsync(old.Check(Manifest(Release(_v101))));
            await old.ApplyAndRestartAsync(update);

            handOffStart = Assert.Single(_processes.Started);
            Assert.Equal((NewPath, _directory), (handOffStart.ExecutablePath, handOffStart.WorkingDirectory));
            Assert.Equal(new[] { "CpLauncher.exe", "CpLauncher.exe.new" }, FileNames());
            Assert.Equal(_v101, File.ReadAllBytes(NewPath));
        }

        // 2. The hand-off (1.0.1 running from .new) waits until the old exe is free, then swaps itself in.
        _processes.Started.Clear();
        UpdateStartupInfo handOffArguments = UpdateStartupInfo.Parse(handOffStart.Arguments);
        Assert.True(handOffArguments.IsHandOff);
        FileStream exitingOld = OpenLikeARunningExe(ExePath);
        _delay.OnWait = wait =>
        {
            if (wait == 2)
            {
                exitingOld.Dispose();
            }
        };
        StartedProcess newLauncherStart;
        using (FileStream runningHandOff = OpenLikeARunningExe(NewPath))
        {
            UpdateService handOff = CreateService(UpdateOptions.GetLauncherPath(handOffStart.ExecutablePath), V101);
            await handOff.InstallAndRestartAsync(handOffArguments);
            exitingOld.Dispose();

            Assert.Equal(2, _delay.Waits.Count);
            Assert.Equal(_v101, File.ReadAllBytes(ExePath));
            Assert.Equal(_v100, File.ReadAllBytes(OldPath));
            Assert.Equal(new[] { "CpLauncher.exe", "CpLauncher.exe.new", "CpLauncher.exe.old" }, FileNames());
            newLauncherStart = Assert.Single(_processes.Started);
            Assert.Equal(ExePath, newLauncherStart.ExecutablePath);
            Assert.Equal(new[] { "--updated-from", "1.0.0" }, newLauncherStart.Arguments);
        }

        // 3. The new 1.0.1 removes the side files; the hand-off image is still exiting for one attempt.
        _delay.Waits.Clear();
        var exitingHandOff = OpenLikeARunningExe(NewPath);
        _delay.OnWait = wait =>
        {
            exitingHandOff.Dispose();
        };
        UpdateService updated = CreateService(ExePath, V101);
        Assert.Equal(LauncherVersion.Parse("1.0.0"), UpdateStartupInfo.Parse(newLauncherStart.Arguments).UpdatedFrom);
        Assert.Equal(UpdateCheckStatus.UpToDate, updated.Check(Manifest(Release(_v101))).Status);

        StartupCleanupResult result = await updated.CleanupOnStartupAsync();
        exitingHandOff.Dispose();

        Assert.Equal((CleanupOutcome.Deleted, CleanupOutcome.Deleted, CleanupOutcome.NotPresent), (result.PreviousVersion, result.NewVersion, result.StagingCopy));
        Assert.Single(_delay.Waits);
        Assert.Equal(new[] { "CpLauncher.exe" }, FileNames());
        Assert.Equal(_v101, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task HandOff_SwapFailure_PutsTheOldExeBackAndRestartsIt()
    {
        File.WriteAllBytes(NewPath, _v101);
        var fileSystem = new FaultingFileSystem(new PhysicalUpdateFileSystem(), failMoveTo: ExePath, failMoveFrom: ExePath + ".tmp");
        var handOff = new UpdateService(Options(ExePath, V101), _instanceLock, fileSystem, _downloads, _processes, _delay);

        UpdateException error = await Assert.ThrowsAsync<UpdateException>(() => handOff.InstallAndRestartAsync(InstallUpdateOver100));

        Assert.False(error.RequiresManualRecovery);
        Assert.Equal(_v100, File.ReadAllBytes(ExePath));
        Assert.Equal(new[] { "CpLauncher.exe", "CpLauncher.exe.new" }, FileNames());
        Assert.Equal(new[] { "--update-failed", "1.0.1" }, Assert.Single(_processes.Started).Arguments);
    }

    [Fact]
    public async Task RollbackCycle_PutsThePreviousVersionBack()
    {
        File.WriteAllBytes(ExePath, _v101);
        File.WriteAllBytes(OldPath, _v100);
        UpdateService updated = CreateService(ExePath, V101);

        updated.RollbackAndRestart();

        StartedProcess handOffStart = Assert.Single(_processes.Started);
        Assert.Equal(OldPath, handOffStart.ExecutablePath);
        _processes.Started.Clear();
        UpdateService handOff = CreateService(UpdateOptions.GetLauncherPath(handOffStart.ExecutablePath), V100);
        using (OpenLikeARunningExe(OldPath))
        {
            await handOff.InstallAndRestartAsync(UpdateStartupInfo.Parse(handOffStart.Arguments));
        }

        Assert.Equal(_v100, File.ReadAllBytes(ExePath));
        Assert.Equal(_v101, File.ReadAllBytes(NewPath));
        Assert.Equal(new[] { "--rolled-back-from", "1.0.1" }, Assert.Single(_processes.Started).Arguments);

        StartupCleanupResult result = await CreateService(ExePath, V100).CleanupOnStartupAsync();

        Assert.Equal((CleanupOutcome.Deleted, CleanupOutcome.Deleted), (result.PreviousVersion, result.NewVersion));
        Assert.Equal(new[] { "CpLauncher.exe" }, FileNames());
    }

    [Fact]
    public async Task InterruptedDownload_LeavesOnlyTheExe()
    {
        _downloads.ServeStream(ReleaseUri, () => ScriptedStream.FailingAfter(_v101, 100_000), _v101.Length);
        UpdateService service = CreateService(ExePath, V100);

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_v101)))));

        Assert.Equal(new[] { "CpLauncher.exe" }, FileNames());
        Assert.Equal(_v100, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task HashMismatch_LeavesOnlyTheExe()
    {
        _downloads.Serve(ReleaseUri, Executable(12));
        UpdateService service = CreateService(ExePath, V100);

        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(service.Check(Manifest(Release(_v101)))));

        Assert.Equal(new[] { "CpLauncher.exe" }, FileNames());
    }

    [Fact]
    public async Task Cleanup_RemovesAReadOnlyOldFile()
    {
        File.WriteAllBytes(OldPath, _v100);
        File.SetAttributes(OldPath, FileAttributes.ReadOnly);

        StartupCleanupResult result = await CreateService(ExePath, V101).CleanupOnStartupAsync();

        Assert.Equal(CleanupOutcome.Deleted, result.PreviousVersion);
        Assert.False(File.Exists(OldPath));
    }

    [Fact]
    public void IsInUse_SeesAnOpenHandleButNotAMissingOrReadOnlyFile()
    {
        var fileSystem = new PhysicalUpdateFileSystem();
        string readOnly = Path.Combine(_directory, "read-only.bin");
        File.WriteAllBytes(readOnly, new byte[] { 1 });
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        Assert.False(fileSystem.IsInUse(ExePath));
        using (OpenLikeARunningExe(ExePath))
        {
            Assert.True(fileSystem.IsInUse(ExePath));
        }

        Assert.False(fileSystem.IsInUse(NewPath));
        Assert.False(fileSystem.IsInUse(readOnly));
        File.SetAttributes(readOnly, FileAttributes.Normal);
    }

    private static FileStream OpenLikeARunningExe(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private UpdateService CreateService(string exePath, LauncherVersion current)
    {
        return new UpdateService(Options(exePath, current), _instanceLock, new PhysicalUpdateFileSystem(), _downloads, _processes, _delay);
    }

    private string[] FileNames()
    {
        return Directory.GetFiles(_directory).Select(path => Path.GetFileName(path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Real files, but one rename always fails (a disk or antivirus error at the worst moment).</summary>
    private sealed class FaultingFileSystem : IUpdateFileSystem
    {
        private readonly IUpdateFileSystem _inner;
        private readonly string _failMoveFrom;
        private readonly string _failMoveTo;

        public FaultingFileSystem(IUpdateFileSystem inner, string failMoveTo, string failMoveFrom)
        {
            _inner = inner;
            _failMoveTo = failMoveTo;
            _failMoveFrom = failMoveFrom;
        }

        public bool FileExists(string path)
        {
            return _inner.FileExists(path);
        }

        public Stream CreateNew(string path)
        {
            return _inner.CreateNew(path);
        }

        public Stream OpenRead(string path)
        {
            return _inner.OpenRead(path);
        }

        public void Move(string source, string destination)
        {
            if (string.Equals(source, _failMoveFrom, StringComparison.OrdinalIgnoreCase) && string.Equals(destination, _failMoveTo, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Simulated failure moving {source} to {destination}.");
            }

            _inner.Move(source, destination);
        }

        public void Delete(string path)
        {
            _inner.Delete(path);
        }

        public bool IsInUse(string path)
        {
            return _inner.IsInUse(path);
        }
    }
}
