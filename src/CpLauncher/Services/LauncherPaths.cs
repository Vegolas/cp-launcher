using L2Mod.Core.Installation;

namespace CpLauncher.Services;

/// <summary>
/// Where the launcher keeps its own data: settings.json, the last verified manifest, downloaded assets, and
/// (through <see cref="InstallerOptions"/>) state.json and backups. Defaults to %LocalAppData%\CpLauncher;
/// <c>--data</c> or the CPLAUNCHER_DATA variable point it elsewhere (tests, manual runs).
/// </summary>
public sealed record LauncherPaths(string DataDirectory)
{
    public const string DataDirectoryVariable = "CPLAUNCHER_DATA";

    public string SettingsPath
    {
        get
        {
            return Path.Combine(DataDirectory, "settings.json");
        }
    }

    /// <summary>See <see cref="LauncherLog"/>.</summary>
    public string LogPath
    {
        get
        {
            return Path.Combine(DataDirectory, "launcher.log");
        }
    }

    /// <summary>Root of the download cache; <see cref="DirectoryAssetStore"/> reads assets from its "assets" folder.</summary>
    public string CacheDirectory
    {
        get
        {
            return Path.Combine(DataDirectory, "cache");
        }
    }

    public string CachedManifestPath
    {
        get
        {
            return Path.Combine(CacheDirectory, ManifestFiles.Manifest);
        }
    }

    public string CachedSignaturePath
    {
        get
        {
            return Path.Combine(CacheDirectory, ManifestFiles.Signature);
        }
    }

    /// <summary>Resolution order: command line, environment variable, %LocalAppData%\CpLauncher.</summary>
    public static LauncherPaths Resolve(string? commandLineDirectory, string? environmentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(commandLineDirectory))
        {
            return new LauncherPaths(Path.GetFullPath(commandLineDirectory));
        }

        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            return new LauncherPaths(Path.GetFullPath(environmentDirectory));
        }

        return new LauncherPaths(InstallerOptions.DefaultDataDirectory);
    }

    /// <summary>Same layout as <see cref="DirectoryAssetStore"/>: cache\assets\&lt;sha256&gt;.png.</summary>
    public string GetCachedAssetPath(string assetPath)
    {
        return Path.Combine(CacheDirectory, "assets", Path.GetFileName(assetPath));
    }

    public InstallerOptions GetInstallerOptions(string clientDirectory)
    {
        return new InstallerOptions(clientDirectory, DataDirectory);
    }
}

/// <summary>File names of the distribution, relative to the manifest source.</summary>
public static class ManifestFiles
{
    public const string Manifest = "manifest.json";
    public const string Signature = "manifest.json.sig";

    /// <summary>A DER P-256 signature has at most 72 bytes; anything much larger is not a signature.</summary>
    public const int MaxSignatureBytes = 256;
}
