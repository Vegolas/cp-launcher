namespace CpLauncher.Services;

/// <summary>What Main sets up before Avalonia starts, handed to the App: command line, updater, HTTP, log.</summary>
public sealed class LauncherProcess
{
    private volatile bool _startupCompleted;

    public LauncherProcess(CommandLineOptions options, LauncherUpdates updates, HttpClient httpClient, LauncherLog log)
    {
        Options = options;
        Updates = updates;
        HttpClient = httpClient;
        Log = log;
    }

    public CommandLineOptions Options { get; }

    public LauncherUpdates Updates { get; }

    public HttpClient HttpClient { get; }

    public LauncherLog Log { get; }

    /// <summary>The main window works. Until then a crash of a freshly updated version rolls it back.</summary>
    public bool StartupCompleted
    {
        get
        {
            return _startupCompleted;
        }
    }

    /// <summary>Marks the start as good and removes the side files of the update; from now on no rollback.</summary>
    public void CompleteStartup()
    {
        if (_startupCompleted)
        {
            return;
        }

        _startupCompleted = true;
        Log.Write($"Launcher {Updates.CurrentVersion} is up.");
        Updates.StartCleanup(Log);
    }
}
