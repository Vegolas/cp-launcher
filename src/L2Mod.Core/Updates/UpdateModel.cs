using L2Mod.Core.Manifests;

namespace L2Mod.Core.Updates;

/// <summary>Retries a file operation that fails because the file is in use (exiting process, antivirus scan).</summary>
public sealed record RetryPolicy(int Attempts, TimeSpan Delay);

/// <summary>Where the launcher is and where its releases come from.</summary>
/// <param name="ExecutablePath">Full path of the launcher exe. In a hand-off process (running from a side file)
/// use <see cref="GetLauncherPath"/> on <c>Environment.ProcessPath</c>.</param>
/// <param name="CurrentVersion">The version of this process (assembly informational version).</param>
/// <param name="ReleaseBaseUri">The folder of manifest.json; <c>launcher.url</c> is relative to it. https only
/// (plain http is accepted for loopback, for local tests).</param>
public sealed record UpdateOptions(string ExecutablePath, LauncherVersion CurrentVersion, Uri ReleaseBaseUri)
{
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";
    public const string StagingSuffix = ".tmp";

    /// <summary>The single-file launcher is about 27 MB.</summary>
    public long MaxDownloadBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>The download fails when no data arrives for this long.</summary>
    public TimeSpan DownloadStallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Deleting side files at startup: the hand-off process may still be exiting.</summary>
    public RetryPolicy CleanupRetry { get; init; } = new RetryPolicy(10, TimeSpan.FromMilliseconds(500));

    /// <summary>Renames and deletes during the swap: an antivirus may hold a fresh file for a moment.</summary>
    public RetryPolicy SwapRetry { get; init; } = new RetryPolicy(5, TimeSpan.FromMilliseconds(200));

    /// <summary>The hand-off waits this long for the previous launcher to exit (its exe to be free).</summary>
    public RetryPolicy PreviousExitWait { get; init; } = new RetryPolicy(60, TimeSpan.FromMilliseconds(500));

    /// <summary><c>&lt;exe&gt;.new</c>: the verified download, which runs as the hand-off; after a rollback the
    /// version that was rolled back.</summary>
    public string NewPath
    {
        get
        {
            return ExecutablePath + NewSuffix;
        }
    }

    /// <summary><c>&lt;exe&gt;.old</c>: the previous version after an update; the rollback hand-off runs from it.</summary>
    public string OldPath
    {
        get
        {
            return ExecutablePath + OldSuffix;
        }
    }

    /// <summary><c>&lt;exe&gt;.tmp</c>: the hand-off's verified copy of itself, renamed to the exe path.</summary>
    public string StagingPath
    {
        get
        {
            return ExecutablePath + StagingSuffix;
        }
    }

    public string ExecutableDirectory
    {
        get
        {
            return Path.GetDirectoryName(ExecutablePath) ?? string.Empty;
        }
    }

    /// <summary>The launcher exe path for a process path: a hand-off runs from <c>&lt;exe&gt;.new</c> or
    /// <c>&lt;exe&gt;.old</c>, everything else from the exe itself.</summary>
    public static string GetLauncherPath(string processPath)
    {
        foreach (string suffix in new[] { NewSuffix, OldSuffix })
        {
            if (processPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return processPath.Substring(0, processPath.Length - suffix.Length);
            }
        }

        return processPath;
    }
}

public enum UpdateCheckStatus
{
    /// <summary>The manifest has no launcher entry.</summary>
    NoRelease,

    UpToDate,

    UpdateAvailable,

    /// <summary>The running launcher is newer than the release (a test build); never downgraded.</summary>
    CurrentIsNewer,

    /// <summary>The release was rolled back on this machine; the caller asked to skip it.</summary>
    Skipped,

    /// <summary>The release version is not strict SemVer (e.g. leading zeros); ignored.</summary>
    InvalidRelease,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    LauncherVersion Current,
    LauncherVersion? Available,
    LauncherRelease? Release,
    string Message)
{
    public bool IsUpdateAvailable
    {
        get
        {
            return Status == UpdateCheckStatus.UpdateAvailable;
        }
    }
}

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>A downloaded release whose hash matched the signed manifest. Only <see cref="UpdateService"/> makes these.</summary>
public sealed class DownloadedUpdate
{
    internal DownloadedUpdate(LauncherVersion version, string path, string sha256, long length)
    {
        Version = version;
        Path = path;
        Sha256 = sha256;
        Length = length;
    }

    public LauncherVersion Version { get; }

    public string Path { get; }

    public string Sha256 { get; }

    public long Length { get; }
}

public enum CleanupOutcome
{
    NotPresent,
    Deleted,

    /// <summary>Still in use after all retries; left for the next start.</summary>
    StillLocked,
}

/// <summary>What happened to each side file: <c>.old</c>, <c>.new</c> and <c>.tmp</c>.</summary>
public sealed record StartupCleanupResult(
    CleanupOutcome PreviousVersion,
    CleanupOutcome NewVersion,
    CleanupOutcome StagingCopy,
    IReadOnlyList<string> Messages);

/// <summary>An update step failed. The launcher at the exe path is unchanged unless
/// <see cref="RequiresManualRecovery"/> is set; then the message says which file to rename.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UpdateException(string message, Exception innerException, bool requiresManualRecovery)
        : base(message, innerException)
    {
        RequiresManualRecovery = requiresManualRecovery;
    }

    public bool RequiresManualRecovery { get; }
}
