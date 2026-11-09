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

    /// <summary>
    /// Operations of the selection for one file, in manifest order. The selection holds the ids of selected packs
    /// and, for packs with options, the <see cref="Pack.GetSelectionId"/> of each selected option; unknown ids are
    /// ignored.
    /// </summary>
    public IReadOnlyList<IPatchOperation> GetOperations(string file, IReadOnlyCollection<string> selectedIds)
    {
        var selected = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        var operations = new List<IPatchOperation>();
        foreach (Pack pack in Packs)
        {
            if (!pack.HasOptions)
            {
                if (selected.Contains(pack.Id))
                {
                    AddForFile(pack.Operations, file, operations);
                }

                continue;
            }

            foreach (PackOption option in pack.Options)
            {
                if (selected.Contains(pack.GetSelectionId(option)))
                {
                    AddForFile(option.Operations, file, operations);
                }
            }
        }

        return operations;
    }

    /// <summary>Every pack id and option selection id, i.e. the selection of everything.</summary>
    public IReadOnlyList<string> GetAllSelectionIds()
    {
        var ids = new List<string>();
        foreach (Pack pack in Packs)
        {
            if (!pack.HasOptions)
            {
                ids.Add(pack.Id);
                continue;
            }

            foreach (PackOption option in pack.Options)
            {
                ids.Add(pack.GetSelectionId(option));
            }
        }

        return ids;
    }

    private static void AddForFile(IReadOnlyList<IPatchOperation> source, string file, List<IPatchOperation> operations)
    {
        foreach (IPatchOperation operation in source)
        {
            if (string.Equals(operation.File, file, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(operation);
            }
        }
    }
}

public sealed record LauncherRelease(string Version, string Url, string Sha256);

public sealed record Announcement(DateOnly Date, string Title, string Text);

/// <param name="Description">What the pack changes, in the leader's words (plain text, line breaks allowed); optional.</param>
/// <param name="Operations">Everything the pack can change; for a pack with <see cref="Options"/>, the operations of
/// all options in order (use <see cref="WithOptions"/>).</param>
public sealed record Pack(string Id, string Name, bool Default, IReadOnlyList<IPatchOperation> Operations, string? Description = null)
{
    /// <summary>
    /// Choices the member ticks one by one (e.g. one status of the icons over the target); empty for a pack that is
    /// selected as a whole. Launchers before 1.2.0 skip a pack with options (it has no "operations").
    /// </summary>
    public IReadOnlyList<PackOption> Options { get; init; } = Array.Empty<PackOption>();

    public bool HasOptions
    {
        get
        {
            return Options.Count > 0;
        }
    }

    public static Pack WithOptions(string id, string name, bool isDefault, IReadOnlyList<PackOption> options, string? description = null)
    {
        var operations = options.SelectMany(option => option.Operations).ToList();
        return new Pack(id, name, isDefault, operations, description)
        {
            Options = options,
        };
    }

    /// <summary>"&lt;pack id&gt;/&lt;option id&gt;": how an option is selected (settings, <see cref="Manifest.GetOperations"/>).</summary>
    public string GetSelectionId(PackOption option)
    {
        return $"{Id}/{option.Id}";
    }
}

/// <param name="Default">Whether the option is selected until the member decides.</param>
public sealed record PackOption(string Id, string Name, bool Default, IReadOnlyList<IPatchOperation> Operations, string? Description = null);

public sealed record OptionPreset(string Id, string Name, IReadOnlyList<IniValue> Values);

/// <summary>A parsed manifest plus what was skipped and why (shown to the user, not fatal).</summary>
public sealed record ManifestReadResult(Manifest Manifest, IReadOnlyList<ManifestNote> Notes)
{
    /// <summary>The English <see cref="ManifestNote.Message"/> of every note, in order.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            return Notes.Select(note => note.Message).ToList();
        }
    }
}

/// <summary>What a <see cref="ManifestNote"/> is about.</summary>
public enum ManifestNoteCode
{
    /// <summary>A newer schema: only the envelope (enabled, launcher) was read, no packs or presets.</summary>
    UnsupportedSchema,

    /// <summary>The launcher entry of a newer-schema manifest could not be read; no self-update from it.</summary>
    LauncherIgnored,

    /// <summary>supportedOriginals names a file that is not patchable.</summary>
    OriginalIgnored,

    AnnouncementSkipped,

    /// <summary>More announcements than <see cref="ManifestLimits.MaxAnnouncements"/>; the rest are not shown.</summary>
    AnnouncementLimit,

    PackSkipped,

    /// <summary>More packs than <see cref="ManifestLimits.MaxPacks"/>; the rest are skipped.</summary>
    PackLimit,

    PresetSkipped,

    /// <summary>More presets than <see cref="ManifestLimits.MaxPresets"/>; the rest are skipped.</summary>
    PresetLimit,
}

/// <summary>Why an announcement, pack or preset was skipped.</summary>
public enum ManifestSkipReason
{
    /// <summary>The note is not about a skipped item (schema, limits, supportedOriginals).</summary>
    None,

    /// <summary>A missing, malformed or out-of-range value.</summary>
    InvalidValue,

    /// <summary>An operation kind this launcher does not know (probably needs a newer launcher).</summary>
    UnknownOperation,

    /// <summary>A field this launcher does not know in an operation (probably needs a newer launcher).</summary>
    UnknownField,

    /// <summary>An operation on a client file outside the whitelist.</summary>
    FileNotAllowed,

    /// <summary>More operations or values than the limits allow.</summary>
    OverLimit,

    /// <summary>Another pack or preset already has this id.</summary>
    DuplicateId,

    /// <summary>A preset sets a window position key (GamePlayViewport*).</summary>
    ForbiddenKey,
}

/// <summary>
/// One thing the parser skipped or ignored, structured so a UI can describe it in its own language;
/// <see cref="Message"/> is the English text (also in <see cref="ManifestReadResult.Messages"/>).
/// </summary>
/// <param name="ItemId">The pack or preset id as written in the manifest (shortened, possibly invalid), or null.</param>
/// <param name="ItemName">Its name (an announcement's title) as written, shortened and without control
/// characters, or null.</param>
public sealed record ManifestNote(ManifestNoteCode Code, ManifestSkipReason Reason, string? ItemId, string? ItemName, string Message);

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

    public int MaxOptionsPerPack { get; init; } = 128;

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
