using System.Text;

namespace CpLauncher.Services;

/// <summary>
/// launcher.log in the data folder: starts, updates (also from the hand-off process, which has no window) and
/// unexpected errors, for support. Writing never throws; a line that cannot be written is lost. Above
/// <see cref="MaxBytes"/> the file moves to launcher.log.1 and a new one starts.
/// </summary>
public sealed class LauncherLog
{
    public const long MaxBytes = 512 * 1024;

    private const int WriteAttempts = 5;

    private readonly object _gate = new object();

    public LauncherLog(string? path)
    {
        Path = path;
    }

    /// <summary>A log that writes nothing (tests).</summary>
    public static LauncherLog None
    {
        get
        {
            return new LauncherLog(null);
        }
    }

    public string? Path { get; }

    public void Write(string message)
    {
        if (Path is null)
        {
            return;
        }

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            for (int attempt = 1; attempt <= WriteAttempts; attempt++)
            {
                try
                {
                    Append(Path, line);
                    return;
                }
                catch (IOException) when (attempt < WriteAttempts)
                {
                    // Another launcher process (hand-off, restart) is writing a line right now.
                    Thread.Sleep(20);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Still busy, or the folder is not writable: the line is lost.
                    return;
                }
            }
        }
    }

    private static void Append(string path, string line)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new FileInfo(path);
        if (file.Exists && file.Length > MaxBytes)
        {
            File.Move(path, path + ".1", true);
        }

        File.AppendAllText(path, line, Encoding.UTF8);
    }
}
