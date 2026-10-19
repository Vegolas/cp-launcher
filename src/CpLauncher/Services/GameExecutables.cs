using System.Diagnostics;

namespace CpLauncher.Services;

public sealed record GameExecutableOption(string RelativePath, string Label);

/// <summary>
/// What "Uruchom L2Dex" starts: L2Updater.exe in the client root, the way the CP starts Dex (confirmed 2026-10-19).
/// data\L2Launcher.exe stays selectable in the launcher settings.
/// </summary>
public static class GameExecutables
{
    public const string Updater = "L2Updater.exe";

    public static string DexLauncher
    {
        get
        {
            return Path.Combine("data", "L2Launcher.exe");
        }
    }

    public static string Default
    {
        get
        {
            return Updater;
        }
    }

    public static IReadOnlyList<GameExecutableOption> Options
    {
        get
        {
            return new[]
            {
                new GameExecutableOption(Updater, "L2Updater.exe (folder główny)"),
                new GameExecutableOption(DexLauncher, "data\\L2Launcher.exe"),
            };
        }
    }

    /// <summary>
    /// The full path of <paramref name="relativePath"/> inside the client folder, or null when it is not a
    /// relative .exe path that stays inside the folder.
    /// </summary>
    public static string? Resolve(string clientDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        if (!string.Equals(Path.GetExtension(relativePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientDirectory)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full;
    }
}

/// <summary>Starts the game; faked in tests.</summary>
public interface IGameStarter
{
    void Start(string executablePath, string workingDirectory);
}

public sealed class ProcessGameStarter : IGameStarter
{
    public void Start(string executablePath, string workingDirectory)
    {
        // Shell execute, so an updater that requires elevation gets its UAC prompt instead of failing.
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };
        using Process? process = Process.Start(startInfo);
    }
}
