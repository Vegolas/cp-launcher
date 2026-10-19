using System.Text;
using L2Mod.Core.Installation;

namespace CpLauncher.Services;

/// <summary>The composed services plus notes for the main screen (bad arguments, damaged settings).</summary>
public sealed record LauncherStartup(LauncherServices Services, IReadOnlyList<string> Notes);

/// <summary>Builds the services from the command line; the window supplies the dialogs.</summary>
public static class LauncherBootstrap
{
    public static LauncherStartup Create(
        CommandLineOptions options,
        IFileSystem fileSystem,
        IProcessMonitor processes,
        IGameStarter gameStarter,
        IUserDialogs dialogs,
        string? environmentDataDirectory)
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

        var sources = new ManifestSourceFactory(fileSystem, ManifestSourceFactory.CreateHttpClient());
        var services = new LauncherServices(fileSystem, processes, paths, keys, sources, gameStarter, dialogs);
        if (services.SettingsProblem is not null)
        {
            notes.Add(services.SettingsProblem);
        }

        ApplyOverrides(services, options, notes);
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
}
