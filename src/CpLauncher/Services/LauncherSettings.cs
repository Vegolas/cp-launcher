using System.Text.Json;
using System.Text.Json.Serialization;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Updates;

namespace CpLauncher.Services;

/// <summary>
/// Contents of settings.json. <see cref="PackSelection"/> holds only the choices the member applied; packs that
/// are not in it (new packs) use their manifest default.
/// </summary>
public sealed record LauncherSettings
{
    public string? ClientDirectory { get; init; }

    /// <summary>Base URL (http/https) or a local folder with manifest.json; null uses <see cref="LauncherDefaults.ManifestSource"/>.</summary>
    public string? ManifestSource { get; init; }

    /// <summary>Executable started by "Uruchom L2Dex", relative to the client folder.</summary>
    public string GameExecutable { get; init; } = GameExecutables.Default;

    public Dictionary<string, bool> PackSelection { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>A launcher version not to offer again on this computer: its install failed or it was rolled back.</summary>
    public string? SkippedLauncherVersion { get; init; }

    [JsonIgnore]
    public LauncherVersion? SkippedVersion
    {
        get
        {
            if (LauncherVersion.TryParse(SkippedLauncherVersion, out LauncherVersion? version))
            {
                return version;
            }

            return null;
        }
    }

    public static LauncherSettings Default
    {
        get
        {
            return new LauncherSettings();
        }
    }

    [JsonIgnore]
    public string? EffectiveManifestSource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ManifestSource))
            {
                return ManifestSource;
            }

            return LauncherDefaults.ManifestSource;
        }
    }

    /// <summary>Selected pack ids in manifest order.</summary>
    public IReadOnlyList<string> GetSelectedPackIds(Manifest manifest)
    {
        var selected = new List<string>();
        foreach (Pack pack in manifest.Packs)
        {
            if (IsSelected(pack))
            {
                selected.Add(pack.Id);
            }
        }

        return selected;
    }

    public bool IsSelected(Pack pack)
    {
        if (PackSelection.TryGetValue(pack.Id, out bool selected))
        {
            return selected;
        }

        return pack.Default;
    }

    /// <summary>Records the choice for every pack of the manifest; choices for packs no longer listed are kept.</summary>
    public LauncherSettings WithSelection(Manifest manifest, IReadOnlyCollection<string> selectedPackIds)
    {
        var selection = new Dictionary<string, bool>(PackSelection, StringComparer.Ordinal);
        foreach (Pack pack in manifest.Packs)
        {
            selection[pack.Id] = selectedPackIds.Contains(pack.Id);
        }

        return this with { PackSelection = selection };
    }
}

public static class LauncherDefaults
{
    /// <summary>
    /// TODO(release): the public address of the CP distribution (manifest.json, manifest.json.sig, assets/), set
    /// before the first release. It is compiled in, so released launchers keep asking this address: it has to stay
    /// valid for as long as those versions are in use (a domain of our own survives a move away from r2.dev).
    /// A member's own value in the settings overrides it.
    /// </summary>
    public const string? ManifestSource = null;
}

public sealed record SettingsLoadResult(LauncherSettings Settings, string? Problem);

public sealed class SettingsStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _path;

    public SettingsStore(IFileSystem fileSystem, string path)
    {
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>
    /// The saved settings, or defaults when there are none. Unlike state.json, damaged settings only cost the
    /// member a few choices, so they are replaced by defaults (and the problem is reported).
    /// </summary>
    public SettingsLoadResult Load()
    {
        if (!_fileSystem.FileExists(_path))
        {
            return new SettingsLoadResult(LauncherSettings.Default, null);
        }

        try
        {
            LauncherSettings? settings = JsonSerializer.Deserialize(_fileSystem.ReadAllBytes(_path), LauncherJsonContext.Default.LauncherSettings);
            if (settings is null)
            {
                return new SettingsLoadResult(LauncherSettings.Default, "Plik ustawień był pusty; przywrócono ustawienia domyślne.");
            }

            if (settings.PackSelection is null || string.IsNullOrWhiteSpace(settings.GameExecutable))
            {
                settings = settings with
                {
                    PackSelection = settings.PackSelection ?? new Dictionary<string, bool>(StringComparer.Ordinal),
                    GameExecutable = string.IsNullOrWhiteSpace(settings.GameExecutable) ? GameExecutables.Default : settings.GameExecutable,
                };
            }

            return new SettingsLoadResult(settings, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(LauncherSettings.Default, $"Nie udało się odczytać ustawień ({exception.Message}); przywrócono ustawienia domyślne.");
        }
    }

    public void Save(LauncherSettings settings)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, LauncherJsonContext.Default.LauncherSettings);
        VerifiedWrite.Write(_fileSystem, _path, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJsonContext : JsonSerializerContext
{
}
