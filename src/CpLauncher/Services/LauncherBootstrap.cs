using System.Text;
using L2Mod.Core.Installation;
using L2Mod.Core.Updates;

namespace CpLauncher.Services;

/// <summary>The composed services plus notes for the main screen (bad arguments, damaged settings).</summary>
public sealed record LauncherStartup(LauncherServices Services, IReadOnlyList<string> Notes);

/// <summary>What the process provides to the services: the updater, the way to quit, HTTP and the log.</summary>
public sealed record LauncherHost(LauncherUpdates Updates, ILauncherLifetime Lifetime, HttpClient HttpClient, LauncherLog Log);

/// <summary>Builds the services from the command line; the window supplies the dialogs.</summary>
public static class LauncherBootstrap
{
    public static LauncherStartup Create(
        CommandLineOptions options,
        IFileSystem fileSystem,
        IProcessMonitor processes,
        IGameStarter gameStarter,
        IUserDialogs dialogs,
        string? environmentDataDirectory,
        LauncherHost host)
    {
        var notes = new List<string>(options.Problems);
        LauncherPaths paths = LauncherPaths.Resolve(options.DataDirectory, environmentDataDirectory);
        var keys = new List<TrustedKey> { TrustedKeys.Embedded };
        if (options.DevPublicKeyPath is not null)
        {
            try
            {
                string pem = Encoding.UTF8.GetString(fileSystem.ReadAllBytes(options.DevPublicKeyPath));
                keys.Add(TrustedKeys.Development(pem, Path.GetFileName(options.DevPublicKeyPath)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                notes.Add($"Klucz deweloperski {options.DevPublicKeyPath} pominięty: {exception.Message}");
            }
        }

        var sources = new ManifestSourceFactory(fileSystem, host.HttpClient);
        var services = new LauncherServices(fileSystem, processes, paths, keys, sources, gameStarter, dialogs, host.Updates, host.Lifetime)
        {
            Log = host.Log,
        };
        if (services.SettingsProblem is not null)
        {
            notes.Add(services.SettingsProblem);
        }

        ApplyOverrides(services, options, notes);
        RememberSkippedVersion(services, notes);
        return new LauncherStartup(services, notes);
    }

    private static void ApplyOverrides(LauncherServices services, CommandLineOptions options, List<string> notes)
    {
        if (options.ClientDirectory is null && options.ManifestSource is null)
        {
            return;
        }

        try
        {
            services.Settings.Update(settings =>
            {
                LauncherSettings updated = settings;
                if (options.ClientDirectory is not null)
                {
                    updated = updated with { ClientDirectory = Path.GetFullPath(options.ClientDirectory) };
                }

                if (options.ManifestSource is not null)
                {
                    updated = updated with { ManifestSource = options.ManifestSource };
                }

                return updated;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            notes.Add($"Nie udało się zapisać ustawień z linii poleceń: {exception.Message}");
        }
    }

    /// <summary>A version whose install failed or that was rolled back is not offered again.</summary>
    private static void RememberSkippedVersion(LauncherServices services, List<string> notes)
    {
        LauncherVersion? skipped = services.Updates.VersionToSkip;
        if (skipped is null)
        {
            return;
        }

        services.Log.Write($"Launcher {skipped} will not be offered again on this computer.");
        try
        {
            services.Settings.Update(settings => settings with { SkippedLauncherVersion = skipped.ToString() });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Nie udało się zapamiętać pominiętej wersji launchera: {exception.Message}");
        }
    }
}
