using L2Mod.Core.Updates;

namespace CpLauncher.Services;

/// <summary>Creates the updater for one release folder (the folder of manifest.json); tests pass fakes.</summary>
public delegate UpdateService UpdateServiceFactory(Uri releaseBaseUri);

/// <summary>Ends the application (the window closes, Main releases the instance lock and returns).</summary>
public interface ILauncherLifetime
{
    void Shutdown();
}

/// <summary>
/// The self-update of L2Mod.Core.Updates as the launcher uses it: the running version, how this process was
/// started (normally, after an update, after a rollback or after a failed update) and an
/// <see cref="UpdateService"/> per release source. The release folder is the folder of the manifest, so it
/// changes with the source in the settings; only an https source (http on loopback for local tests) can
/// deliver an update.
/// </summary>
public sealed class LauncherUpdates
{
    /// <summary>
    /// The release folder for updaters that download nothing: the check of a manifest without a usable source,
    /// the startup cleanup, the hand-off and the rollback. The .invalid domain never resolves (RFC 2606).
    /// </summary>
    public static readonly Uri NoReleaseSource = new Uri("https://release-source.invalid/");

    private readonly UpdateServiceFactory _factory;
    private Task<StartupCleanupResult?> _cleanup = Task.FromResult<StartupCleanupResult?>(null);
    private bool _restartTaken;

    public LauncherUpdates(LauncherVersion currentVersion, UpdateStartupInfo startup, UpdateServiceFactory factory)
    {
        CurrentVersion = currentVersion;
        Startup = startup;
        _factory = factory;
    }

    public LauncherVersion CurrentVersion { get; }

    public UpdateStartupInfo Startup { get; }

    /// <summary>A version this start says not to offer again: its install failed, or it was rolled back.</summary>
    public LauncherVersion? VersionToSkip
    {
        get
        {
            return Startup.FailedVersion ?? Startup.RolledBackFrom;
        }
    }

    /// <summary>
    /// The folder releases are downloaded from for a manifest source, or null when that source cannot deliver
    /// one (a local folder, or plain http to another computer).
    /// </summary>
    public static Uri? GetReleaseBase(IManifestSource? source)
    {
        if (source is not HttpManifestSource http)
        {
            return null;
        }

        Uri uri = http.BaseUri;
        bool secure = uri.Scheme == Uri.UriSchemeHttps;
        bool localTest = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!secure && !localTest)
        {
            return null;
        }

        return uri;
    }

    /// <summary>An updater for <paramref name="releaseBaseUri"/>, or for <see cref="NoReleaseSource"/>.</summary>
    public UpdateService Create(Uri? releaseBaseUri)
    {
        return _factory(releaseBaseUri ?? NoReleaseSource);
    }

    /// <summary>
    /// How this process was started by another launcher (after an update, a rollback, a failed update), once:
    /// the first dashboard shows it, later ones do not. Null after a normal start.
    /// </summary>
    public UpdateStartupInfo? TakeRestartInfo()
    {
        if (_restartTaken || !Startup.IsRestart)
        {
            return null;
        }

        _restartTaken = true;
        return Startup;
    }

    /// <summary>
    /// Deletes the side files of an update or rollback (<c>.old</c>, <c>.new</c>, <c>.tmp</c>) in the
    /// background. Call it once the main window works: until then a crashing new version can still roll back.
    /// </summary>
    public Task<StartupCleanupResult?> StartCleanup(LauncherLog log)
    {
        _cleanup = CleanupAsync(log);
        return _cleanup;
    }

    /// <summary>A download must not start while the cleanup may still delete <c>.new</c>.</summary>
    public Task WaitForCleanupAsync()
    {
        return _cleanup;
    }

    /// <summary>
    /// The hand-off (<see cref="UpdateStartupInfo.IsHandOff"/>), before any UI: waits for the previous launcher
    /// to exit, puts this process's file at the exe path and starts it. On a failure Core puts the previous exe
    /// back and starts it with <c>--update-failed</c>; only when even that is impossible the member has to
    /// rename a file, and <paramref name="showManualRecovery"/> gets the text to show. Returns the exit code.
    /// </summary>
    public int RunHandOff(LauncherLog log, Action<string> showManualRecovery)
    {
        try
        {
            Create(null).InstallAndRestartAsync(Startup).GetAwaiter().GetResult();
            log.Write($"Hand-off: launcher {CurrentVersion} installed and started.");
            return 0;
        }
        catch (UpdateException exception)
        {
            log.Write("Hand-off failed: " + exception.Message);
            if (exception.RequiresManualRecovery)
            {
                showManualRecovery(
                    "Aktualizacja launchera nie powiodła się, a poprzedniej wersji nie dało się przywrócić automatycznie.\n\n"
                    + exception.Message
                    + $"\n\nSzczegóły: {log.Path}");
            }

            return 1;
        }
        catch (InvalidOperationException exception)
        {
            log.Write("Hand-off refused: " + exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// After a crash: when this is a freshly updated version (<c>--updated-from</c>) whose window never came up
    /// (<paramref name="startupCompleted"/> false) and the previous version is still there, starts it to put
    /// itself back (<c>--install-rollback</c>); exit right after true. It then skips this version.
    /// </summary>
    public bool TryRollBackAfterCrash(bool startupCompleted, LauncherLog log)
    {
        if (startupCompleted || Startup.UpdatedFrom is null)
        {
            return false;
        }

        try
        {
            UpdateService service = Create(null);
            if (!service.CanRollback)
            {
                log.Write("Rollback impossible: there is no previous version (.old).");
                return false;
            }

            service.RollbackAndRestart();
            log.Write($"Rollback: launcher {Startup.UpdatedFrom} was started to put itself back.");
            return true;
        }
        catch (Exception exception) when (exception is UpdateException or InvalidOperationException)
        {
            log.Write("Rollback failed: " + exception.Message);
            return false;
        }
    }

    private async Task<StartupCleanupResult?> CleanupAsync(LauncherLog log)
    {
        try
        {
            StartupCleanupResult result = await Create(null).CleanupOnStartupAsync();
            foreach (string message in result.Messages)
            {
                log.Write("Cleanup: " + message);
            }

            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log.Write("Cleanup failed: " + exception.Message);
            return null;
        }
    }
}
