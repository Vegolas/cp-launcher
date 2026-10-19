namespace L2Mod.Core.Updates;

/// <summary>
/// What the command line says about the launcher that started this one. The protocol between versions:
/// <list type="table">
/// <item><term><c>--install-update 1.0.0</c></term><description>hand-off: this process runs from <c>&lt;exe&gt;.new</c>
/// and installs itself over launcher 1.0.0 (<see cref="UpdateService.InstallAndRestartAsync"/>), no UI;</description></item>
/// <item><term><c>--install-rollback 1.0.1</c></term><description>hand-off: this process runs from <c>&lt;exe&gt;.old</c>
/// and puts itself back over launcher 1.0.1, no UI;</description></item>
/// <item><term><c>--updated-from 1.0.0</c></term><description>the installed new version, after an update;</description></item>
/// <item><term><c>--rolled-back-from 1.0.1</c></term><description>the previous version, after a rollback;</description></item>
/// <item><term><c>--update-failed 1.0.1</c></term><description>the hand-off could not install 1.0.1 and restarted this
/// (unchanged) launcher.</description></item>
/// </list>
/// Anything else on the command line is ignored, and so is an option with a missing or malformed version, so a
/// bad argument never stops the launcher.
/// </summary>
public sealed record UpdateStartupInfo(
    LauncherVersion? UpdatedFrom,
    LauncherVersion? RolledBackFrom,
    LauncherVersion? InstallUpdateOver,
    LauncherVersion? InstallRollbackOver,
    LauncherVersion? FailedVersion)
{
    public const string UpdatedFromOption = "--updated-from";
    public const string RolledBackFromOption = "--rolled-back-from";
    public const string InstallUpdateOption = "--install-update";
    public const string InstallRollbackOption = "--install-rollback";
    public const string UpdateFailedOption = "--update-failed";

    /// <summary>How long a launcher started by another one waits for it to release the instance lock.</summary>
    public static readonly TimeSpan RestartLockTimeout = TimeSpan.FromSeconds(30);

    public static UpdateStartupInfo None
    {
        get
        {
            return new UpdateStartupInfo(null, null, null, null, null);
        }
    }

    /// <summary>This process only installs itself and exits: call <see cref="UpdateService.InstallAndRestartAsync"/>
    /// before any UI.</summary>
    public bool IsHandOff
    {
        get
        {
            return InstallUpdateOver is not null || InstallRollbackOver is not null;
        }
    }

    /// <summary>Started by another launcher process rather than by the user.</summary>
    public bool IsRestart
    {
        get
        {
            return IsHandOff || UpdatedFrom is not null || RolledBackFrom is not null || FailedVersion is not null;
        }
    }

    /// <summary>Zero for a normal start (a second launcher gives up at once), <see cref="RestartLockTimeout"/>
    /// after a restart (the starting process still holds the lock while it exits).</summary>
    public TimeSpan InstanceLockTimeout
    {
        get
        {
            return IsRestart ? RestartLockTimeout : TimeSpan.Zero;
        }
    }

    public static UpdateStartupInfo Parse(IReadOnlyList<string> arguments)
    {
        return new UpdateStartupInfo(
            Find(arguments, UpdatedFromOption),
            Find(arguments, RolledBackFromOption),
            Find(arguments, InstallUpdateOption),
            Find(arguments, InstallRollbackOption),
            Find(arguments, UpdateFailedOption));
    }

    private static LauncherVersion? Find(IReadOnlyList<string> arguments, string option)
    {
        LauncherVersion? found = null;
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], option, StringComparison.Ordinal) && LauncherVersion.TryParse(arguments[i + 1], out LauncherVersion? version))
            {
                found = version;
            }
        }

        return found;
    }
}
