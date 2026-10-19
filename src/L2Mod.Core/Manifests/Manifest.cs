using L2Mod.Core.Ini;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Manifests;

/// <summary>
/// The distribution manifest (schema 1). <see cref="Schema"/>, <see cref="Enabled"/> and <see cref="Launcher"/>
/// form the stable envelope: they are read even from a newer schema, so old launchers still obey the kill
/// switch and can update themselves.
/// </summary>
public sealed record Manifest(
    int Schema,
    string Version,
    bool Enabled,
    LauncherRelease? Launcher,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SupportedOriginals,
    IReadOnlyList<Announcement> Announcements,
    IReadOnlyList<Pack> Packs,
    IReadOnlyList<OptionPreset> OptionPresets)
{
    public bool IsSupportedOriginal(string file, string sha256)
    {
        if (!SupportedOriginals.TryGetValue(file, out IReadOnlyList<string>? hashes))
        {
            return false;
        }

        foreach (string hash in hashes)
        {
            if (string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Operations of the selected packs for one file, in manifest order. Unknown pack ids are ignored.</summary>
    public IReadOnlyList<IIconOperation> GetOperations(string file, IReadOnlyCollection<string> selectedPackIds)
    {
        var selected = new HashSet<string>(selectedPackIds, StringComparer.Ordinal);
        var operations = new List<IIconOperation>();
        foreach (Pack pack in Packs)
        {
            if (!selected.Contains(pack.Id))
            {
                continue;
            }

            foreach (IIconOperation operation in pack.Operations)
            {
                if (string.Equals(operation.File, file, StringComparison.OrdinalIgnoreCase))
                {
                    operations.Add(operation);
                }
            }
        }

        return operations;
    }
}

public sealed record LauncherRelease(string Version, string Url, string Sha256);

public sealed record Announcement(DateOnly Date, string Title, string Text);

public sealed record Pack(string Id, string Name, bool Default, IReadOnlyList<IIconOperation> Operations);

public sealed record OptionPreset(string Id, string Name, IReadOnlyList<IniValue> Values);

/// <summary>A parsed manifest plus what was skipped and why (shown to the user, not fatal).</summary>
public sealed record ManifestReadResult(Manifest Manifest, IReadOnlyList<string> Messages);

/// <summary>The manifest cannot be used at all (bad JSON, missing envelope fields, too large, bad signature).</summary>
public sealed class ManifestException : Exception
{
    public ManifestException(string message)
        : base(message)
    {
    }

    public ManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Limits for data that comes from the network. Exceeding a pack or preset limit skips that item.</summary>
public sealed record ManifestLimits
{
    public int MaxManifestBytes { get; init; } = 1024 * 1024;

    public int MaxPacks { get; init; } = 64;

    public int MaxOperationsPerPack { get; init; } = 512;

    public int MaxOperationsTotal { get; init; } = 2048;

    public int MaxAnnouncements { get; init; } = 50;

    public int MaxPresets { get; init; } = 32;

    public int MaxPresetValues { get; init; } = 128;

    public static ManifestLimits Default
    {
        get
        {
            return new ManifestLimits();
        }
    }
}
