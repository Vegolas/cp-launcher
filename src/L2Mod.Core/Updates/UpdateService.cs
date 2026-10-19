using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using L2Mod.Core.Manifests;

namespace L2Mod.Core.Updates;

/// <summary>
/// Updates the portable launcher exe in place, from the <c>launcher</c> entry of an already verified manifest.
/// <para>
/// A running exe is never renamed or replaced. Windows would allow the rename, but a .NET single-file exe loads
/// its assemblies lazily from its own path, so after a rename the old process cannot load anything new, not even
/// to report an error or shut down (seen in the Phase 4 spike). Instead the new version installs itself after the
/// old one has exited (hand-off):
/// </para>
/// <list type="number">
/// <item>old launcher, <see cref="Check"/>: release version vs the running one; never a downgrade.</item>
/// <item>old launcher, <see cref="DownloadAndVerifyAsync"/>: streams the release into <c>&lt;exe&gt;.new</c> with a
/// size limit and a stall timeout and checks its SHA-256 against the manifest; on any failure the partial file is
/// deleted. The exe is not touched.</item>
/// <item>old launcher, <see cref="ApplyAndRestartAsync"/>: checks the hash on disk again and starts <c>.new</c>
/// with <c>--install-update &lt;old version&gt;</c>; the caller then exits as usual.</item>
/// <item>hand-off, the new version running from <c>.new</c>, <see cref="InstallAndRestartAsync"/>: waits until
/// the old exe is free, copies itself to <c>&lt;exe&gt;.tmp</c> and verifies the copy, renames the exe to
/// <c>&lt;exe&gt;.old</c> and <c>.tmp</c> to the exe, starts it with <c>--updated-from &lt;old version&gt;</c>
/// and exits. On a failure the old exe is put back and started with <c>--update-failed &lt;new version&gt;</c>.</item>
/// <item>new launcher, <see cref="CleanupOnStartupAsync"/>: deletes <c>.old</c>, <c>.new</c> and <c>.tmp</c>,
/// retrying while the hand-off is still exiting. Until then <see cref="RollbackAndRestart"/> can go back the
/// same way, with the hand-off running from <c>.old</c>.</item>
/// </list>
/// <para>
/// Side files found on disk are never trusted, only deleted (at startup and before they are written again).
/// Every step that touches files needs the single-instance lock (see <see cref="NamedMutexInstanceLockProvider"/>),
/// so a second launcher cannot race on them. One operation at a time per service.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private const int BufferSize = 81920;

    private readonly UpdateOptions _options;
    private readonly IInstanceLock _instanceLock;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly IDownloadClient _downloads;
    private readonly IProcessStarter _processes;
    private readonly IDelay _delay;
    private readonly Uri _baseUri;
    private int _busy;

    public UpdateService(
        UpdateOptions options,
        IInstanceLock instanceLock,
        IUpdateFileSystem fileSystem,
        IDownloadClient downloads,
        IProcessStarter processes,
        IDelay delay)
    {
        if (!Path.IsPathFullyQualified(options.ExecutablePath))
        {
            throw new ArgumentException($"Executable path '{options.ExecutablePath}' must be absolute.", nameof(options));
        }

        if (options.MaxDownloadBytes <= 0 || options.DownloadStallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The download limit and the stall timeout must be positive.", nameof(options));
        }

        if (options.CleanupRetry.Attempts < 1 || options.SwapRetry.Attempts < 1 || options.PreviousExitWait.Attempts < 1)
        {
            throw new ArgumentException("Retry policies need at least one attempt.", nameof(options));
        }

        _options = options;
        _instanceLock = instanceLock;
        _fileSystem = fileSystem;
        _downloads = downloads;
        _processes = processes;
        _delay = delay;
        _baseUri = NormalizeBaseUri(options.ReleaseBaseUri);
    }

    public UpdateOptions Options
    {
        get
        {
            return _options;
        }
    }

    /// <summary>The previous version (<c>.old</c>) is still there, so <see cref="RollbackAndRestart"/> can work.</summary>
    public bool CanRollback
    {
        get
        {
            return _fileSystem.FileExists(_options.OldPath);
        }
    }

    /// <summary>The production wiring: real files, real processes, downloads through <paramref name="httpClient"/>.</summary>
    public static UpdateService CreateDefault(UpdateOptions options, IInstanceLock instanceLock, HttpClient httpClient)
    {
        return new UpdateService(
            options,
            instanceLock,
            new PhysicalUpdateFileSystem(),
            new HttpDownloadClient(httpClient),
            new SystemProcessStarter(),
            new SystemDelay());
    }

    /// <summary>
    /// Compares the release in <paramref name="manifest"/> (verified by the caller with
    /// <see cref="Signing.ManifestSignature"/>) with the running version. The kill switch does not matter
    /// here: a disabled manifest can still ship a fixed launcher.
    /// </summary>
    /// <param name="skippedVersion">A version the user rolled back from (or whose install failed); reported as
    /// <see cref="UpdateCheckStatus.Skipped"/> instead of being offered again.</param>
    public UpdateCheckResult Check(Manifest manifest, LauncherVersion? skippedVersion = null)
    {
        LauncherVersion current = _options.CurrentVersion;
        LauncherRelease? release = manifest.Launcher;
        if (release is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NoRelease, current, null, null, "The manifest offers no launcher release.");
        }

        if (!LauncherVersion.TryParse(release.Version, out LauncherVersion? available))
        {
            return new UpdateCheckResult(UpdateCheckStatus.InvalidRelease, current, null, release, $"Launcher release version '{release.Version}' is not valid; ignored.");
        }

        if (!ContentHash.IsSha256Hex(release.Sha256) || !TryResolveDownloadUri(release, out _))
        {
            return new UpdateCheckResult(UpdateCheckStatus.InvalidRelease, current, available, release, $"Launcher release {available} has a bad url or hash; ignored.");
        }

        int comparison = available.CompareTo(current);
        if (comparison == 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, available, release, $"The launcher is up to date ({current}).");
        }

        if (comparison < 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.CurrentIsNewer, current, available, release, $"This launcher ({current}) is newer than the release ({available}).");
        }

        if (skippedVersion is not null && available == skippedVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Skipped, current, available, release, $"Launcher {available} is skipped on this computer.");
        }

        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, current, available, release, $"Launcher {available} is available (this is {current}).");
    }

    /// <summary>
    /// Downloads the release from <paramref name="check"/> into <c>&lt;exe&gt;.new</c> and verifies it.
    /// Throws <see cref="UpdateException"/> (too large, stalled, HTTP error, hash mismatch, file in use) or
    /// <see cref="OperationCanceledException"/>; in both cases no <c>.new</c> is left behind.
    /// </summary>
    public async Task<DownloadedUpdate> DownloadAndVerifyAsync(
        UpdateCheckResult check,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!check.IsUpdateAvailable || check.Release is null || check.Available is null)
        {
            throw new InvalidOperationException($"No update to download: {check.Message}");
        }

        EnterOperation();
        try
        {
            LauncherRelease release = check.Release;
            if (!TryResolveDownloadUri(release, out Uri? uri))
            {
                throw new UpdateException($"Launcher url '{release.Url}' is not under {_baseUri}.");
            }

            string newPath = _options.NewPath;
            await DeleteOrThrowAsync(newPath, cancellationToken).ConfigureAwait(false);
            long length;
            try
            {
                length = await DownloadToFileAsync(uri, newPath, release.Sha256, progress, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(newPath);
                throw;
            }

            return new DownloadedUpdate(check.Available, newPath, release.Sha256.ToLowerInvariant(), length);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Checks the download once more and starts it as the hand-off (<c>--install-update &lt;current&gt;</c>).
    /// Save what needs saving, then shut down right after this returns: the hand-off waits for this process to
    /// release the instance lock and to exit, then installs itself and starts the new launcher. Nothing at the
    /// exe path changes in this process. On <see cref="UpdateException"/> the download is gone; download again.
    /// </summary>
    public async Task ApplyAndRestartAsync(DownloadedUpdate update)
    {
        EnterOperation();
        try
        {
            string newPath = _options.NewPath;
            if (!string.Equals(update.Path, newPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{update.Path} was not downloaded by this updater.");
            }

            await VerifyDownloadAsync(update).ConfigureAwait(false);
            try
            {
                StartLauncher(newPath, UpdateStartupInfo.InstallUpdateOption, _options.CurrentVersion);
            }
            catch (Exception error)
            {
                TryDelete(newPath);
                throw new UpdateException($"The downloaded launcher did not start ({error.Message}); nothing was changed.", error);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Starts the previous version (<c>.old</c>) as the hand-off (<c>--install-rollback &lt;current&gt;</c>); shut
    /// down right after this returns. The hand-off parks this version as <c>.new</c> and starts the previous one
    /// with <c>--rolled-back-from &lt;current&gt;</c>. Pass that version to <see cref="Check"/> as skipped (and
    /// keep it in the settings), or the same release is offered again.
    /// </summary>
    public void RollbackAndRestart()
    {
        EnterOperation();
        try
        {
            string oldPath = _options.OldPath;
            if (!_fileSystem.FileExists(oldPath))
            {
                throw new UpdateException("There is no previous launcher version to go back to.");
            }

            try
            {
                StartLauncher(oldPath, UpdateStartupInfo.InstallRollbackOption, _options.CurrentVersion);
            }
            catch (Exception error)
            {
                throw new UpdateException($"The previous launcher did not start ({error.Message}); nothing was changed.", error);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// The hand-off (<see cref="UpdateStartupInfo.IsHandOff"/>): installs this process's own file (<c>.new</c> for
    /// an update, <c>.old</c> for a rollback) at the exe path and starts it; exit right after this returns, without
    /// UI. A working exe stays at the exe path throughout: the copy is staged and verified first, then two renames
    /// swap it in. On <see cref="UpdateException"/> the previous exe is back and was started with
    /// <c>--update-failed &lt;this version&gt;</c> (unless it was still running, or
    /// <see cref="UpdateException.RequiresManualRecovery"/> is set).
    /// </summary>
    public async Task InstallAndRestartAsync(UpdateStartupInfo startup)
    {
        HandOff handOff = GetHandOff(startup);
        EnterOperation();
        try
        {
            string exePath = _options.ExecutablePath;
            string stagingPath = _options.StagingPath;
            if (!await WaitForPreviousExitAsync(exePath).ConfigureAwait(false))
            {
                double seconds = _options.PreviousExitWait.Delay.TotalSeconds * _options.PreviousExitWait.Attempts;
                throw new UpdateException($"{Path.GetFileName(exePath)} is still running after {seconds:0} s; launcher {_options.CurrentVersion} was not installed.");
            }

            try
            {
                await DeleteOrThrowAsync(stagingPath, CancellationToken.None).ConfigureAwait(false);
                await CopyVerifiedAsync(handOff.Source, stagingPath).ConfigureAwait(false);
                await DeleteOrThrowAsync(handOff.Parking, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is UpdateException || IsFileError(error))
            {
                TryDelete(stagingPath);
                throw RestartPreviousAfterFailure(error);
            }

            bool parked = false;
            if (_fileSystem.FileExists(exePath))
            {
                try
                {
                    await MoveWithRetryAsync(exePath, handOff.Parking).ConfigureAwait(false);
                    parked = true;
                }
                catch (Exception error) when (IsFileError(error))
                {
                    TryDelete(stagingPath);
                    throw RestartPreviousAfterFailure(error);
                }
            }

            try
            {
                await MoveWithRetryAsync(stagingPath, exePath).ConfigureAwait(false);
            }
            catch (Exception error) when (IsFileError(error))
            {
                if (parked)
                {
                    await MoveBackAsync(handOff.Parking, exePath, error).ConfigureAwait(false);
                }

                TryDelete(stagingPath);
                throw RestartPreviousAfterFailure(error);
            }

            try
            {
                StartLauncher(exePath, handOff.SuccessOption, handOff.Replaced);
            }
            catch (Exception error)
            {
                if (!parked)
                {
                    throw new UpdateException($"Launcher {_options.CurrentVersion} was installed but did not start ({error.Message}).", error);
                }

                await UndoInstallAsync(exePath, stagingPath, handOff.Parking, error).ConfigureAwait(false);
                TryDelete(stagingPath);
                throw RestartPreviousAfterFailure(error);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Deletes the side files <c>.old</c>, <c>.new</c> and <c>.tmp</c>, retrying while they are in use (the
    /// hand-off may still be exiting). A file still in use after the retries stays for the next start.
    /// After an update (<see cref="UpdateStartupInfo.UpdatedFrom"/>) call this once the new version has shown it
    /// works (main window up): before that, <see cref="RollbackAndRestart"/> can still go back.
    /// </summary>
    public async Task<StartupCleanupResult> CleanupOnStartupAsync(CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            var messages = new List<string>();
            CleanupOutcome previous = await DeleteLeftoverAsync(_options.OldPath, "The previous launcher version", messages, cancellationToken).ConfigureAwait(false);
            CleanupOutcome next = await DeleteLeftoverAsync(_options.NewPath, "A downloaded or rolled back launcher version", messages, cancellationToken).ConfigureAwait(false);
            CleanupOutcome staging = await DeleteLeftoverAsync(_options.StagingPath, "An unfinished launcher copy", messages, cancellationToken).ConfigureAwait(false);
            return new StartupCleanupResult(previous, next, staging, messages);
        }
        finally
        {
            ExitOperation();
        }
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException($"Release base '{uri}' must be an absolute URI.", nameof(uri));
        }

        bool secure = uri.Scheme == Uri.UriSchemeHttps;
        bool localTest = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!secure && !localTest)
        {
            throw new ArgumentException($"Release base '{uri}' must use https.", nameof(uri));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"Release base '{uri}' must not have a query or fragment.", nameof(uri));
        }

        if (uri.AbsolutePath.EndsWith('/'))
        {
            return uri;
        }

        return new Uri(uri.AbsoluteUri + "/");
    }

    private static bool IsFileError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException;
    }

    private HandOff GetHandOff(UpdateStartupInfo startup)
    {
        if (startup.InstallUpdateOver is not null && startup.InstallRollbackOver is null)
        {
            return new HandOff(_options.NewPath, _options.OldPath, UpdateStartupInfo.UpdatedFromOption, startup.InstallUpdateOver);
        }

        if (startup.InstallRollbackOver is not null && startup.InstallUpdateOver is null)
        {
            return new HandOff(_options.OldPath, _options.NewPath, UpdateStartupInfo.RolledBackFromOption, startup.InstallRollbackOver);
        }

        throw new InvalidOperationException("This process was not started to install an update or a rollback.");
    }

    /// <summary>The manifest parser already allows only relative paths without '..'; checked again here because
    /// <see cref="LauncherRelease"/> can be built by anyone.</summary>
    private bool TryResolveDownloadUri(LauncherRelease release, [NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(_baseUri, release.Url, out uri) || !_baseUri.IsBaseOf(uri) || uri == _baseUri)
        {
            uri = null;
            return false;
        }

        if (!string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.Query))
        {
            uri = null;
            return false;
        }

        return true;
    }

    private async Task<long> DownloadToFileAsync(
        Uri uri,
        string path,
        string expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long limit = _options.MaxDownloadBytes;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            stall.CancelAfter(_options.DownloadStallTimeout);
            using DownloadResponse response = await _downloads.OpenAsync(uri, stall.Token).ConfigureAwait(false);
            if (response.ContentLength > limit)
            {
                throw new UpdateException($"The launcher download is {response.ContentLength} bytes; the limit is {limit}.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            long total = 0;
            using (Stream output = _fileSystem.CreateNew(path))
            {
                while (true)
                {
                    stall.CancelAfter(_options.DownloadStallTimeout);
                    int read = await response.Content.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > limit)
                    {
                        throw new UpdateException($"The launcher download exceeds the limit of {limit} bytes.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new DownloadProgress(total, response.ContentLength));
                }

                await FlushToDiskAsync(output).ConfigureAwait(false);
            }

            if (response.ContentLength is long announced && announced != total)
            {
                throw new UpdateException($"The launcher download ended after {total} of {announced} bytes.");
            }

            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"The downloaded launcher has SHA-256 {actual}, the signed manifest says {expectedSha256.ToLowerInvariant()}; discarded.");
            }

            return total;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"The launcher download stalled (no data for {_options.DownloadStallTimeout.TotalSeconds:0} s).", error);
        }
        catch (HttpRequestException error)
        {
            throw new UpdateException($"The launcher download failed: {error.Message}", error);
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new UpdateException($"The launcher download failed: {error.Message}", error);
        }
    }

    /// <summary>The file may have changed since the download (another program, disk error): hash it again.</summary>
    private async Task VerifyDownloadAsync(DownloadedUpdate update)
    {
        string actual;
        try
        {
            actual = await HashFileAsync(update.Path).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error))
        {
            TryDelete(update.Path);
            throw new UpdateException($"The downloaded launcher cannot be read ({error.Message}); download it again.", error);
        }

        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(update.Path);
            throw new UpdateException("The downloaded launcher changed on disk since it was verified; download it again.");
        }
    }

    /// <summary>Copies the running side file and checks that the copy on disk reads back the same.</summary>
    private async Task CopyVerifiedAsync(string source, string destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        using (Stream input = _fileSystem.OpenRead(source))
        using (Stream output = _fileSystem.CreateNew(destination))
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            await FlushToDiskAsync(output).ConfigureAwait(false);
        }

        string expected = Convert.ToHexStringLower(hash.GetHashAndReset());
        string actual = await HashFileAsync(destination).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new UpdateException($"The copy {Path.GetFileName(destination)} does not read back like {Path.GetFileName(source)}.");
        }
    }

    /// <summary>The file is renamed over the exe soon; its data must be on disk before that.</summary>
    private static async Task FlushToDiskAsync(Stream output)
    {
        await output.FlushAsync().ConfigureAwait(false);
        if (output is FileStream file)
        {
            file.Flush(true);
        }
    }

    private async Task<string> HashFileAsync(string path)
    {
        using (Stream input = _fileSystem.OpenRead(path))
        {
            byte[] digest = await SHA256.HashDataAsync(input).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest);
        }
    }

    private void StartLauncher(string executablePath, string option, LauncherVersion version)
    {
        var arguments = new[] { option, version.ToString() };
        _processes.Start(executablePath, arguments, _options.ExecutableDirectory);
    }

    /// <summary>
    /// After a failed hand-off, with the previous exe back in place: start it with <c>--update-failed</c>, so the
    /// user has a launcher again and learns why. Returns the exception to throw.
    /// </summary>
    private UpdateException RestartPreviousAfterFailure(Exception cause)
    {
        string exePath = _options.ExecutablePath;
        string message = $"Launcher {_options.CurrentVersion} could not be installed ({cause.Message})";
        if (!_fileSystem.FileExists(exePath))
        {
            return new UpdateException($"{message}, and there is no launcher at {exePath}. Rename '{_options.NewPath}' or '{_options.OldPath}' to '{Path.GetFileName(exePath)}' by hand.", cause, requiresManualRecovery: true);
        }

        try
        {
            StartLauncher(exePath, UpdateStartupInfo.UpdateFailedOption, _options.CurrentVersion);
        }
        catch (Exception error)
        {
            return new UpdateException($"{message}; the previous launcher is in place but did not start ({error.Message}).", cause);
        }

        return new UpdateException($"{message}; the previous launcher was started again.", cause);
    }

    /// <summary>After the exe was moved to <paramref name="parkedPath"/> and the replacement failed.</summary>
    private async Task MoveBackAsync(string parkedPath, string exePath, Exception cause)
    {
        try
        {
            await MoveWithRetryAsync(parkedPath, exePath).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new UpdateException(
                $"The launcher could not be put back ({error.Message}) after: {cause.Message}. Rename '{parkedPath}' to '{Path.GetFileName(exePath)}' by hand.",
                error,
                requiresManualRecovery: true);
        }
    }

    /// <summary>After a completed swap whose launcher did not start: the installed exe goes back to staging, the
    /// parked one to the exe path.</summary>
    private async Task UndoInstallAsync(string exePath, string stagingPath, string parkedPath, Exception cause)
    {
        try
        {
            await MoveWithRetryAsync(exePath, stagingPath).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error))
        {
            throw new UpdateException(
                $"Launcher {_options.CurrentVersion} did not start ({cause.Message}) and could not be taken out again ({error.Message}). The previous version is '{parkedPath}'.",
                error,
                requiresManualRecovery: true);
        }

        await MoveBackAsync(parkedPath, exePath, cause).ConfigureAwait(false);
    }

    /// <summary>The previous launcher released the lock, but may still be exiting; renaming its exe meanwhile
    /// would break its last assembly loads.</summary>
    private async Task<bool> WaitForPreviousExitAsync(string exePath)
    {
        RetryPolicy policy = _options.PreviousExitWait;
        for (int attempt = 1; ; attempt++)
        {
            if (!_fileSystem.IsInUse(exePath))
            {
                return true;
            }

            if (attempt >= policy.Attempts)
            {
                return false;
            }

            await _delay.WaitAsync(policy.Delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task MoveWithRetryAsync(string source, string destination)
    {
        RetryPolicy policy = _options.SwapRetry;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _fileSystem.Move(source, destination);
                return;
            }
            catch (Exception error) when (IsFileError(error) && attempt < policy.Attempts && _fileSystem.FileExists(source))
            {
                // In use for a moment (antivirus, exiting process): wait and try again.
            }

            // The swap must finish or be undone, so it is not cancellable.
            await _delay.WaitAsync(policy.Delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DeleteOrThrowAsync(string path, CancellationToken cancellationToken)
    {
        (CleanupOutcome outcome, Exception? error) = await DeleteWithRetryAsync(path, _options.SwapRetry, cancellationToken).ConfigureAwait(false);
        if (outcome == CleanupOutcome.StillLocked)
        {
            throw new UpdateException($"{Path.GetFileName(path)} is in use and cannot be replaced.", error!);
        }
    }

    private async Task<(CleanupOutcome Outcome, Exception? LastError)> DeleteWithRetryAsync(string path, RetryPolicy policy, CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(path))
        {
            return (CleanupOutcome.NotPresent, null);
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _fileSystem.Delete(path);
                return (CleanupOutcome.Deleted, null);
            }
            catch (Exception error) when (IsFileError(error))
            {
                if (attempt >= policy.Attempts)
                {
                    return (CleanupOutcome.StillLocked, error);
                }
            }

            await _delay.WaitAsync(policy.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CleanupOutcome> DeleteLeftoverAsync(string path, string description, List<string> messages, CancellationToken cancellationToken)
    {
        (CleanupOutcome outcome, Exception? error) = await DeleteWithRetryAsync(path, _options.CleanupRetry, cancellationToken).ConfigureAwait(false);
        if (outcome == CleanupOutcome.Deleted)
        {
            messages.Add($"{description} ({Path.GetFileName(path)}) was removed.");
        }
        else if (outcome == CleanupOutcome.StillLocked)
        {
            messages.Add($"{description} ({Path.GetFileName(path)}) is still in use ({error!.Message}); it will be removed at the next start.");
        }

        return outcome;
    }

    /// <summary>Best effort: used on paths that are already failing; the next start deletes what stays.</summary>
    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.Delete(path);
        }
        catch (Exception error) when (IsFileError(error))
        {
            // Left for CleanupOnStartupAsync.
        }
    }

    private void EnterOperation()
    {
        if (!_instanceLock.IsHeld)
        {
            throw new InvalidOperationException("The single-instance lock is not held; another launcher may be running.");
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another update operation is still running.");
        }
    }

    private void ExitOperation()
    {
        Volatile.Write(ref _busy, 0);
    }

    /// <param name="Source">The side file this hand-off runs from.</param>
    /// <param name="Parking">Where the exe it replaces goes.</param>
    private sealed record HandOff(string Source, string Parking, string SuccessOption, LauncherVersion Replaced);
}
