using Avalonia;
using CpLauncher.Services;
using L2Mod.Core.Updates;

namespace CpLauncher;

internal static class Program
{
    private const string Caption = "Veggie launcher";

    /// <summary>
    /// The order matters. The single-instance lock comes first: a launcher started by another one (update,
    /// rollback) waits for it, the starting process holds it until it exits. The update hand-off
    /// (<c>--install-update</c>, <c>--install-rollback</c>) runs next, without any UI. Only then Avalonia. When a
    /// freshly updated version crashes before its window works, the previous version (still kept as
    /// <c>.old</c>) is started to put itself back.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        UpdateStartupInfo startup = UpdateStartupInfo.Parse(args);
        CommandLineOptions options = CommandLineOptions.Parse(args);
        LauncherPaths paths = LauncherPaths.Resolve(options.DataDirectory, Environment.GetEnvironmentVariable(LauncherPaths.DataDirectoryVariable));
        var log = new LauncherLog(paths.LogPath);
        LauncherVersion version = LauncherBuild.CurrentVersion;
        string processPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CpLauncher.exe");
        string launcherPath = UpdateOptions.GetLauncherPath(processPath);
        log.Write($"Launcher {version} started from {processPath}{DescribeStart(startup)}.");

        using IInstanceLock? acquired = new NamedMutexInstanceLockProvider().TryAcquire(NamedMutexInstanceLockProvider.DefaultName, startup.InstanceLockTimeout);
        if (acquired is null)
        {
            log.Write("Another launcher is running; this one exits.");
            if (!startup.IsHandOff)
            {
                NativeMessages.ShowInformation(Caption, "Veggie launcher już działa – jego okno znajdziesz na pasku zadań.");
            }

            return 1;
        }

        IInstanceLock instanceLock = acquired;
        using HttpClient httpClient = ManifestSourceFactory.CreateHttpClient();
        IReadOnlyList<string> forwarded = options.ForwardedArguments;
        var updates = new LauncherUpdates(version, startup, releaseBase =>
        {
            var updateOptions = new UpdateOptions(launcherPath, version, releaseBase)
            {
                ForwardedArguments = forwarded,
            };
            return UpdateService.CreateDefault(updateOptions, instanceLock, httpClient);
        });
        if (startup.IsHandOff)
        {
            return updates.RunHandOff(log, text =>
            {
                NativeMessages.ShowError(Caption, text);
            });
        }

        var process = new LauncherProcess(options, updates, httpClient, log);
        try
        {
            return BuildAvaloniaApp(process).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            log.Write($"Launcher {version} crashed: {exception}");
            if (updates.TryRollBackAfterCrash(process.StartupCompleted, log))
            {
                return 1;
            }

            throw;
        }
    }

    /// <summary>Used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }

    private static AppBuilder BuildAvaloniaApp(LauncherProcess process)
    {
        return AppBuilder.Configure(() =>
        {
            return new App(process);
        }).UsePlatformDetect().LogToTrace();
    }

    private static string DescribeStart(UpdateStartupInfo startup)
    {
        if (startup.InstallUpdateOver is not null)
        {
            return $" to install itself over {startup.InstallUpdateOver}";
        }

        if (startup.InstallRollbackOver is not null)
        {
            return $" to put itself back over {startup.InstallRollbackOver}";
        }

        if (startup.UpdatedFrom is not null)
        {
            return $" (updated from {startup.UpdatedFrom})";
        }

        if (startup.RolledBackFrom is not null)
        {
            return $" (rolled back from {startup.RolledBackFrom})";
        }

        if (startup.FailedVersion is not null)
        {
            return $" (the install of {startup.FailedVersion} failed)";
        }

        return string.Empty;
    }
}
