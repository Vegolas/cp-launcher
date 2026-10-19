using L2Mod.Core.Installation;

namespace CpLauncher.Services;

/// <summary>Dialogs the view models need; implemented by the window, faked in tests.</summary>
public interface IUserDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>The chosen folder, or null when the member cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>The current settings, saved on every change.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;

    public SettingsService(SettingsStore store, LauncherSettings current)
    {
        _store = store;
        Current = current;
    }

    public LauncherSettings Current { get; private set; }

    public void Update(Func<LauncherSettings, LauncherSettings> change)
    {
        LauncherSettings updated = change(Current);
        _store.Save(updated);
        Current = updated;
    }
}

/// <summary>Everything the view models use from the outside world; tests build it from fakes.</summary>
public sealed class LauncherServices
{
    public LauncherServices(
        IFileSystem fileSystem,
        IProcessMonitor processes,
        LauncherPaths paths,
        IReadOnlyList<TrustedKey> trustedKeys,
        ManifestSourceFactory sources,
        IGameStarter gameStarter,
        IUserDialogs dialogs)
    {
        FileSystem = fileSystem;
        Processes = processes;
        Paths = paths;
        TrustedKeys = trustedKeys;
        Sources = sources;
        GameStarter = gameStarter;
        Dialogs = dialogs;
        SettingsStore = new SettingsStore(fileSystem, paths.SettingsPath);
        SettingsLoadResult loaded = SettingsStore.Load();
        Settings = new SettingsService(SettingsStore, loaded.Settings);
        SettingsProblem = loaded.Problem;
        Manifests = new ManifestService(fileSystem, paths, trustedKeys);
        Assets = new AssetCache(fileSystem, paths);
        Previews = new PackPreviewRenderer(fileSystem);
    }

    public IFileSystem FileSystem { get; }

    public IProcessMonitor Processes { get; }

    public LauncherPaths Paths { get; }

    public IReadOnlyList<TrustedKey> TrustedKeys { get; }

    public ManifestSourceFactory Sources { get; }

    public IGameStarter GameStarter { get; }

    public IUserDialogs Dialogs { get; }

    public SettingsStore SettingsStore { get; }

    public SettingsService Settings { get; }

    /// <summary>Why the stored settings could not be used (shown once on the main screen).</summary>
    public string? SettingsProblem { get; }

    public ManifestService Manifests { get; }

    public AssetCache Assets { get; }

    public PackPreviewRenderer Previews { get; }

    public Version CurrentVersion { get; init; } = LauncherVersion.Current;
}
