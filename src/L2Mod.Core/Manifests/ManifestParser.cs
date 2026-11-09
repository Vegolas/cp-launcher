using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using L2Mod.Core.Badges;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Operations;
using L2Mod.Core.Recipes;
using L2Mod.Core.Updates;

namespace L2Mod.Core.Manifests;

/// <summary>
/// Strict reader of manifest.json (schema 1); the bytes come from the network, so everything is checked.
/// Problems with the envelope (schema, version, enabled, launcher, supportedOriginals, packs array) reject the
/// whole manifest. A pack with an unknown op, an unknown field in an operation, a file outside the whitelist,
/// bad values, a bad option or too many operations is skipped with a message; so is a preset with a forbidden key
/// (GamePlayViewport*) or an announcement with bad values. Unknown top-level and pack fields are ignored,
/// so later additions do not break older launchers. A manifest with another schema yields only the
/// envelope (enabled, launcher) and no packs or presets.
/// </summary>
public static partial class ManifestParser
{
    public const int SupportedSchema = 1;
    public const string ForbiddenPresetKeyPrefix = "GamePlayViewport";
    public const int MaxDescriptionLength = 1000;

    private const int MaxNameLength = 100;
    private const int MaxTitleLength = 200;
    private const int MaxTextLength = 4000;
    private const int MaxPresetValueLength = 256;
    private const int MinBadgeSize = 4;
    private const int MaxBadgeSize = 32;
    private const int MinTagSize = 4;
    private const int MaxTagSize = 16;

    private static readonly string[] BadgeFields = { "op", "file", "target", "badge", "size", "corner" };
    private static readonly string[] TagFields = { "op", "file", "target", "color", "size", "corner" };
    private static readonly string[] OverlayFields = { "op", "file", "target", "image", "imageSha256", "corner" };
    private static readonly string[] EffectSwapFields = { "op", "file", "skills" };
    private static readonly string[] GoldClickArrowFields = { "op", "file" };
    private static readonly string[] TargetIconFields = { "op", "file", "skill", "effectClass", "seconds" };

    public static ManifestReadResult Parse(byte[] json, ManifestLimits? limits = null)
    {
        ManifestLimits effective = limits ?? ManifestLimits.Default;
        if (json.Length > effective.MaxManifestBytes)
        {
            throw new ManifestException($"Manifest has {json.Length} bytes; the limit is {effective.MaxManifestBytes}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
        }
        catch (JsonException exception)
        {
            throw new ManifestException($"Manifest is not valid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            try
            {
                return ParseRoot(document.RootElement, effective);
            }
            catch (FormatError error)
            {
                throw new ManifestException($"Manifest rejected: {error.Message}");
            }
        }
    }

    private static ManifestReadResult ParseRoot(JsonElement root, ManifestLimits limits)
    {
        RequireObject(root, "the manifest");
        int schema = GetRequiredInt(root, "schema");
        var notes = new List<ManifestNote>();
        if (schema != SupportedSchema)
        {
            return ReadEnvelopeOnly(root, schema, notes);
        }

        string version = GetRequiredString(root, "version", 64);
        if (!VersionPattern().IsMatch(version))
        {
            throw new FormatError($"version '{version}' has invalid characters");
        }

        bool enabled = GetRequiredBool(root, "enabled");
        LauncherRelease? launcher = ReadLauncher(root);
        IReadOnlyDictionary<string, IReadOnlyList<string>> originals = ReadSupportedOriginals(root, notes);
        IReadOnlyList<Announcement> announcements = ReadAnnouncements(root, limits, notes);
        IReadOnlyList<Pack> packs = ReadPacks(root, limits, notes);
        IReadOnlyList<OptionPreset> presets = ReadPresets(root, limits, notes);
        var manifest = new Manifest(schema, version, enabled, launcher, originals, announcements, packs, presets);
        return new ManifestReadResult(manifest, notes);
    }

    private static ManifestReadResult ReadEnvelopeOnly(JsonElement root, int schema, List<ManifestNote> notes)
    {
        notes.Add(Note(ManifestNoteCode.UnsupportedSchema, $"Manifest schema {schema} is not supported by this launcher (it reads schema {SupportedSchema}); packs and presets are skipped. Update the launcher."));
        string version = TryGetString(root, "version") ?? string.Empty;
        bool enabled = true;
        if (root.TryGetProperty("enabled", out JsonElement enabledElement) && enabledElement.ValueKind == JsonValueKind.False)
        {
            enabled = false;
        }

        LauncherRelease? launcher = null;
        try
        {
            launcher = ReadLauncher(root);
        }
        catch (FormatError error)
        {
            notes.Add(Note(ManifestNoteCode.LauncherIgnored, $"Launcher entry ignored: {error.Message}."));
        }

        var manifest = new Manifest(
            schema,
            version,
            enabled,
            launcher,
            new Dictionary<string, IReadOnlyList<string>>(),
            Array.Empty<Announcement>(),
            Array.Empty<Pack>(),
            Array.Empty<OptionPreset>());
        return new ManifestReadResult(manifest, notes);
    }

    private static LauncherRelease? ReadLauncher(JsonElement root)
    {
        if (!root.TryGetProperty("launcher", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(element, "launcher");

        // The same rules as the updater, so the leader's build rejects a version members would ignore.
        string version = GetRequiredString(element, "version", LauncherVersion.MaxLength);
        if (!LauncherVersion.TryParse(version, out _))
        {
            throw new FormatError($"launcher version '{Shorten(version)}' must look like 1.2.3 (SemVer, no leading zeros)");
        }

        string url = GetRequiredString(element, "url", 256);
        if (!IsSafeRelativePath(url))
        {
            throw new FormatError($"launcher url '{url}' must be a relative path without '..'");
        }

        return new LauncherRelease(version, url, GetRequiredSha256(element, "sha256"));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSupportedOriginals(JsonElement root, List<ManifestNote> notes)
    {
        JsonElement element = GetRequiredProperty(root, "supportedOriginals");
        RequireObject(element, "supportedOriginals");
        var originals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!ClientFiles.IsPatchable(property.Name))
            {
                notes.Add(Note(ManifestNoteCode.OriginalIgnored, $"supportedOriginals: '{Shorten(property.Name)}' is not a patchable file; ignored."));
                continue;
            }

            string file = ClientFiles.GetCanonicalName(property.Name);
            if (originals.ContainsKey(file))
            {
                throw new FormatError($"supportedOriginals lists {file} twice");
            }

            RequireArray(property.Value, $"supportedOriginals.{file}");
            var hashes = new List<string>();
            foreach (JsonElement hash in property.Value.EnumerateArray())
            {
                string? value = null;
                if (hash.ValueKind == JsonValueKind.String)
                {
                    value = hash.GetString();
                }

                if (!ContentHash.IsSha256Hex(value))
                {
                    throw new FormatError($"supportedOriginals.{file} must contain SHA-256 hex strings");
                }

                hashes.Add(value!.ToLowerInvariant());
            }

            originals[file] = hashes;
        }

        return originals;
    }

    private static IReadOnlyList<Announcement> ReadAnnouncements(JsonElement root, ManifestLimits limits, List<ManifestNote> notes)
    {
        var announcements = new List<Announcement>();
        if (!root.TryGetProperty("announcements", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return announcements;
        }

        RequireArray(element, "announcements");
        int number = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            number++;
            if (announcements.Count >= limits.MaxAnnouncements)
            {
                notes.Add(Note(ManifestNoteCode.AnnouncementLimit, $"Only the first {limits.MaxAnnouncements} announcements are shown."));
                break;
            }

            try
            {
                announcements.Add(ReadAnnouncement(item));
            }
            catch (FormatError error)
            {
                string message = $"Announcement {number} skipped: {error.Message}.";
                notes.Add(new ManifestNote(ManifestNoteCode.AnnouncementSkipped, error.Reason, null, GetItemText(item, "title"), message));
            }
        }

        return announcements;
    }

    private static Announcement ReadAnnouncement(JsonElement item)
    {
        RequireObject(item, "an announcement");
        string date = GetRequiredString(item, "date", 10);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            throw new FormatError($"date '{date}' must be yyyy-MM-dd");
        }

        string title = GetRequiredText(item, "title", MaxTitleLength, false);
        string text = GetRequiredText(item, "text", MaxTextLength, true);
        return new Announcement(parsed, title, text);
    }

    private static IReadOnlyList<Pack> ReadPacks(JsonElement root, ManifestLimits limits, List<ManifestNote> notes)
    {
        JsonElement element = GetRequiredProperty(root, "packs");
        RequireArray(element, "packs");
        var packs = new List<Pack>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int totalOperations = 0;
        int number = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            number++;
            if (packs.Count >= limits.MaxPacks)
            {
                notes.Add(Note(ManifestNoteCode.PackLimit, $"Only the first {limits.MaxPacks} packs are used; the rest are skipped."));
                break;
            }

            string label = GetLabel(item, number);
            try
            {
                Pack pack = ReadPack(item, limits);
                if (ids.Contains(pack.Id))
                {
                    throw new FormatError("another pack has the same id", ManifestSkipReason.DuplicateId);
                }

                if (totalOperations + pack.Operations.Count > limits.MaxOperationsTotal)
                {
                    throw new FormatError($"the manifest would exceed {limits.MaxOperationsTotal} operations in total", ManifestSkipReason.OverLimit);
                }

                ids.Add(pack.Id);
                totalOperations += pack.Operations.Count;
                packs.Add(pack);
            }
            catch (FormatError error)
            {
                string message = $"Pack '{label}' skipped: {error.Message}.";
                notes.Add(new ManifestNote(ManifestNoteCode.PackSkipped, error.Reason, GetItemText(item, "id"), GetItemText(item, "name"), message));
            }
        }

        return packs;
    }

    /// <summary>
    /// A pack has either "operations" (selected as a whole) or "options" (each selected on its own, with its own
    /// operations); never both. The operation limit counts the operations of all options.
    /// </summary>
    private static Pack ReadPack(JsonElement item, ManifestLimits limits)
    {
        RequireObject(item, "a pack");
        string id = ReadId(item);
        string name = GetRequiredText(item, "name", MaxNameLength, false);
        string? description = ReadDescription(item);
        bool isDefault = GetOptionalBool(item, "default", false);
        bool hasOptions = item.TryGetProperty("options", out JsonElement optionsElement) && optionsElement.ValueKind != JsonValueKind.Null;
        if (!hasOptions)
        {
            IReadOnlyList<IPatchOperation> operations = ReadOperations(item, limits.MaxOperationsPerPack, count =>
            {
                return $"it has {count} operations; the limit is {limits.MaxOperationsPerPack}";
            });
            return new Pack(id, name, isDefault, operations, description);
        }

        if (item.TryGetProperty("operations", out JsonElement operationsElement) && operationsElement.ValueKind != JsonValueKind.Null)
        {
            throw new FormatError("it has both operations and options");
        }

        RequireArray(optionsElement, "options");
        int count = optionsElement.GetArrayLength();
        if (count == 0)
        {
            throw new FormatError("it has no options");
        }

        if (count > limits.MaxOptionsPerPack)
        {
            throw new FormatError($"it has {count} options; the limit is {limits.MaxOptionsPerPack}", ManifestSkipReason.OverLimit);
        }

        var options = new List<PackOption>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int remaining = limits.MaxOperationsPerPack;
        foreach (JsonElement option in optionsElement.EnumerateArray())
        {
            string label = GetLabel(option, options.Count + 1);
            try
            {
                PackOption read = ReadOption(option, isDefault, remaining, limits.MaxOperationsPerPack);
                if (!ids.Add(read.Id))
                {
                    throw new FormatError("another option has the same id", ManifestSkipReason.DuplicateId);
                }

                remaining -= read.Operations.Count;
                options.Add(read);
            }
            catch (FormatError error)
            {
                throw new FormatError($"option '{label}': {error.Message}", error.Reason);
            }
        }

        return Pack.WithOptions(id, name, isDefault, options, description);
    }

    /// <summary>An option of a pack; its "default" falls back to the pack's.</summary>
    private static PackOption ReadOption(JsonElement item, bool packDefault, int remainingOperations, int packLimit)
    {
        RequireObject(item, "an option");
        string id = ReadId(item);
        string name = GetRequiredText(item, "name", MaxNameLength, false);
        string? description = ReadDescription(item);
        bool isDefault = GetOptionalBool(item, "default", packDefault);
        IReadOnlyList<IPatchOperation> operations = ReadOperations(item, remainingOperations, count =>
        {
            return $"the pack has more than {packLimit} operations in its options";
        });
        return new PackOption(id, name, isDefault, operations, description);
    }

    private static IReadOnlyList<IPatchOperation> ReadOperations(JsonElement item, int maxOperations, Func<int, string> overLimitMessage)
    {
        JsonElement operationsElement = GetRequiredProperty(item, "operations");
        RequireArray(operationsElement, "operations");
        int count = operationsElement.GetArrayLength();
        if (count == 0)
        {
            throw new FormatError("it has no operations");
        }

        if (count > maxOperations)
        {
            throw new FormatError(overLimitMessage(count), ManifestSkipReason.OverLimit);
        }

        var operations = new List<IPatchOperation>();
        int number = 0;
        foreach (JsonElement operation in operationsElement.EnumerateArray())
        {
            number++;
            try
            {
                operations.Add(ReadOperation(operation));
            }
            catch (FormatError error)
            {
                throw new FormatError($"operation {number}: {error.Message}", error.Reason);
            }
        }

        return operations;
    }

    /// <summary>
    /// The optional pack description. It only explains the pack, so a bad one is dropped instead of the pack;
    /// the leader tool's read-back check (<c>ManifestWriter.WriteChecked</c>) refuses to publish it anyway.
    /// </summary>
    private static string? ReadDescription(JsonElement item)
    {
        string? text = TryGetString(item, "description");
        if (string.IsNullOrEmpty(text) || text.Length > MaxDescriptionLength)
        {
            return null;
        }

        foreach (char c in text)
        {
            if (char.IsControl(c) && c != '\n')
            {
                return null;
            }
        }

        return text;
    }

    private static IPatchOperation ReadOperation(JsonElement element)
    {
        RequireObject(element, "an operation");
        string kind = GetRequiredString(element, "op", 32);
        string[] fields;
        switch (kind)
        {
            case OperationKinds.Badge:
                fields = BadgeFields;
                break;
            case OperationKinds.Tag:
                fields = TagFields;
                break;
            case OperationKinds.Overlay:
                fields = OverlayFields;
                break;
            case EffectKinds.EffectSwap:
                fields = EffectSwapFields;
                break;
            case EffectKinds.GoldClickArrow:
                fields = GoldClickArrowFields;
                break;
            case EffectKinds.TargetIcon:
                fields = TargetIconFields;
                break;
            default:
            {
                string known = string.Join(", ", OperationKinds.All.Concat(EffectKinds.All));
                throw new FormatError($"unknown op '{Shorten(kind)}' (this launcher knows {known})", ManifestSkipReason.UnknownOperation);
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new FormatError($"unknown field '{Shorten(property.Name)}' in a {kind} operation", ManifestSkipReason.UnknownField);
            }
        }

        string file = GetRequiredString(element, "file", 64);
        if (!ClientFiles.IsPatchable(file))
        {
            throw new FormatError($"file '{Shorten(file)}' is not allowed (allowed: {string.Join(", ", ClientFiles.Patchable)})", ManifestSkipReason.FileNotAllowed);
        }

        file = ClientFiles.GetCanonicalName(file);
        if (kind is EffectKinds.EffectSwap or EffectKinds.GoldClickArrow or EffectKinds.TargetIcon)
        {
            return ReadEffectOperation(element, kind, file);
        }

        if (!ClientFiles.IsIconPackage(file))
        {
            throw new FormatError($"a {kind} operation changes icons in {ClientFiles.IconUtx}, not in {file}");
        }

        string target = ReadTextureName(element, "target");
        switch (kind)
        {
            case OperationKinds.Badge:
            {
                string badge = ReadTextureName(element, "badge");
                int size = ReadBlockSize(element, RecipeDefaults.BadgeSize, MinBadgeSize, MaxBadgeSize);
                IconCorner corner = ReadCorner(element, IconCorner.BottomRight);
                return new BadgeOperation(file, target, badge, new BadgeStyle(size, corner, 1));
            }

            case OperationKinds.Tag:
            {
                string color = GetRequiredString(element, "color", 7);
                if (!ColorPattern().IsMatch(color))
                {
                    throw new FormatError($"color '{color}' must be #RRGGBB");
                }

                int size = ReadBlockSize(element, TagOperation.DefaultSize, MinTagSize, MaxTagSize);
                IconCorner corner = ReadCorner(element, TagOperation.DefaultCorner);
                return new TagOperation(file, target, Rgb.ParseHex(color), size, corner);
            }

            default:
            {
                string image = GetRequiredString(element, "image", 128);
                Match match = AssetPathPattern().Match(image);
                if (!match.Success)
                {
                    throw new FormatError($"image '{Shorten(image)}' must be assets/<sha256>.png");
                }

                string sha256 = GetRequiredSha256(element, "imageSha256");
                if (!string.Equals(match.Groups[1].Value, sha256, StringComparison.Ordinal))
                {
                    throw new FormatError("imageSha256 does not match the image name");
                }

                IconCorner corner = ReadCorner(element, OverlayOperation.DefaultCorner);
                return new OverlayOperation(file, target, image, sha256, corner);
            }
        }
    }

    /// <summary>Effect operations name the one file they change; any other file is an error.</summary>
    private static IEffectOperation ReadEffectOperation(JsonElement element, string kind, string file)
    {
        IEffectOperation operation;
        switch (kind)
        {
            case EffectKinds.EffectSwap:
            {
                JsonElement skills = GetRequiredProperty(element, "skills");
                if (skills.ValueKind != JsonValueKind.Array || skills.GetArrayLength() != 2)
                {
                    throw new FormatError("skills must be an array of two skill ids");
                }

                int first = ReadSkillId(skills[0], "skills");
                int second = ReadSkillId(skills[1], "skills");
                if (first == second)
                {
                    throw new FormatError("skills must name two different skills");
                }

                operation = new EffectSwapOperation(first, second);
                break;
            }

            case EffectKinds.GoldClickArrow:
            {
                operation = new GoldClickArrowOperation();
                break;
            }

            default:
            {
                int skill = ReadSkillId(GetRequiredProperty(element, "skill"), "skill");
                int effectClass = skill;
                if (element.TryGetProperty("effectClass", out JsonElement effectClassElement))
                {
                    effectClass = ReadSkillId(effectClassElement, "effectClass");
                }

                int seconds = GetOptionalInt(element, "seconds", EffectKinds.DefaultIconSeconds);
                if (seconds < EffectKinds.MinIconSeconds || seconds > EffectKinds.MaxIconSeconds)
                {
                    throw new FormatError($"seconds {seconds} must be between {EffectKinds.MinIconSeconds} and {EffectKinds.MaxIconSeconds}");
                }

                operation = new TargetIconOperation(skill, effectClass, seconds);
                break;
            }
        }

        if (!string.Equals(operation.File, file, StringComparison.Ordinal))
        {
            throw new FormatError($"a {kind} operation changes {operation.File}, not {file}");
        }

        return operation;
    }

    private static int ReadSkillId(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int id) || !EffectKinds.IsValidSkillId(id))
        {
            throw new FormatError($"{name} must hold skill ids from 1 to {EffectKinds.MaxSkillId}");
        }

        return id;
    }

    private static IReadOnlyList<OptionPreset> ReadPresets(JsonElement root, ManifestLimits limits, List<ManifestNote> notes)
    {
        var presets = new List<OptionPreset>();
        if (!root.TryGetProperty("optionPresets", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return presets;
        }

        RequireArray(element, "optionPresets");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int number = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            number++;
            if (presets.Count >= limits.MaxPresets)
            {
                notes.Add(Note(ManifestNoteCode.PresetLimit, $"Only the first {limits.MaxPresets} presets are used; the rest are skipped."));
                break;
            }

            string label = GetLabel(item, number);
            try
            {
                OptionPreset preset = ReadPreset(item, limits);
                if (!ids.Add(preset.Id))
                {
                    throw new FormatError("another preset has the same id", ManifestSkipReason.DuplicateId);
                }

                presets.Add(preset);
            }
            catch (FormatError error)
            {
                string message = $"Preset '{label}' skipped: {error.Message}.";
                notes.Add(new ManifestNote(ManifestNoteCode.PresetSkipped, error.Reason, GetItemText(item, "id"), GetItemText(item, "name"), message));
            }
        }

        return presets;
    }

    private static OptionPreset ReadPreset(JsonElement item, ManifestLimits limits)
    {
        RequireObject(item, "a preset");
        string id = ReadId(item);
        string name = GetRequiredText(item, "name", MaxNameLength, false);
        JsonElement sections = GetRequiredProperty(item, "values");
        RequireObject(sections, "values");
        var values = new List<IniValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty section in sections.EnumerateObject())
        {
            if (!IniNamePattern().IsMatch(section.Name))
            {
                throw new FormatError($"section '{Shorten(section.Name)}' has invalid characters");
            }

            RequireObject(section.Value, $"values.{section.Name}");
            foreach (JsonProperty key in section.Value.EnumerateObject())
            {
                string qualified = $"{section.Name}.{key.Name}";
                if (!IniNamePattern().IsMatch(key.Name))
                {
                    throw new FormatError($"key '{Shorten(qualified)}' has invalid characters");
                }

                if (key.Name.StartsWith(ForbiddenPresetKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatError($"it sets {qualified}; window position keys ({ForbiddenPresetKeyPrefix}*) are not allowed", ManifestSkipReason.ForbiddenKey);
                }

                if (key.Value.ValueKind != JsonValueKind.String)
                {
                    throw new FormatError($"{qualified} must be a string");
                }

                string value = key.Value.GetString()!;
                if (value.Length > MaxPresetValueLength || value.Any(char.IsControl))
                {
                    throw new FormatError($"{qualified} is too long or contains control characters");
                }

                if (!seen.Add(qualified))
                {
                    throw new FormatError($"{qualified} is set twice");
                }

                values.Add(new IniValue(section.Name, key.Name, value));
            }
        }

        if (values.Count == 0)
        {
            throw new FormatError("it sets no values");
        }

        if (values.Count > limits.MaxPresetValues)
        {
            throw new FormatError($"it sets {values.Count} values; the limit is {limits.MaxPresetValues}", ManifestSkipReason.OverLimit);
        }

        return new OptionPreset(id, name, values);
    }

    private static string ReadId(JsonElement element)
    {
        string id = GetRequiredString(element, "id", 64);
        if (!IdPattern().IsMatch(id))
        {
            throw new FormatError($"id '{Shorten(id)}' must use lower-case letters, digits and dashes");
        }

        return id;
    }

    private static string ReadTextureName(JsonElement element, string name)
    {
        string value = GetRequiredString(element, name, 128);
        if (!TextureNamePattern().IsMatch(value))
        {
            throw new FormatError($"{name} '{Shorten(value)}' is not a texture name");
        }

        return value;
    }

    private static int ReadBlockSize(JsonElement element, int defaultValue, int minimum, int maximum)
    {
        int size = GetOptionalInt(element, "size", defaultValue);
        if (size < minimum || size > maximum || size % 4 != 0)
        {
            throw new FormatError($"size {size} must be a multiple of 4 between {minimum} and {maximum}");
        }

        return size;
    }

    private static IconCorner ReadCorner(JsonElement element, IconCorner defaultValue)
    {
        if (!element.TryGetProperty("corner", out JsonElement value))
        {
            return defaultValue;
        }

        string? code = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            code = value.GetString();
        }

        if (code is not ("br" or "bl" or "tr" or "tl"))
        {
            throw new FormatError("corner must be br, bl, tr or tl");
        }

        return IconCorners.Parse(code);
    }

    private static string GetRequiredSha256(JsonElement element, string name)
    {
        string value = GetRequiredString(element, name, ContentHash.Sha256HexLength);
        if (!ContentHash.IsSha256Hex(value))
        {
            throw new FormatError($"{name} must be a SHA-256 hex string");
        }

        return value.ToLowerInvariant();
    }

    private static string GetRequiredText(JsonElement element, string name, int maxLength, bool allowNewlines)
    {
        string value = GetRequiredString(element, name, maxLength);
        foreach (char c in value)
        {
            if (char.IsControl(c) && !(allowNewlines && (c == '\n' || c == '\r')))
            {
                throw new FormatError($"{name} contains control characters");
            }
        }

        return value;
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new FormatError($"'{name}' is missing");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement element, string name, int maxLength)
    {
        JsonElement value = GetRequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatError($"'{name}' must be a string");
        }

        string text = value.GetString()!;
        if (text.Length == 0 || text.Length > maxLength)
        {
            throw new FormatError($"'{name}' must have 1 to {maxLength} characters");
        }

        return text;
    }

    private static int GetRequiredInt(JsonElement element, string name)
    {
        JsonElement value = GetRequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            throw new FormatError($"'{name}' must be an integer");
        }

        return number;
    }

    private static int GetOptionalInt(JsonElement element, string name, int defaultValue)
    {
        if (!element.TryGetProperty(name, out _))
        {
            return defaultValue;
        }

        return GetRequiredInt(element, name);
    }

    private static bool GetRequiredBool(JsonElement element, string name)
    {
        JsonElement value = GetRequiredProperty(element, name);
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        throw new FormatError($"'{name}' must be true or false");
    }

    private static bool GetOptionalBool(JsonElement element, string name, bool defaultValue)
    {
        if (!element.TryGetProperty(name, out _))
        {
            return defaultValue;
        }

        return GetRequiredBool(element, name);
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatError($"{what} must be a JSON object");
        }
    }

    private static void RequireArray(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatError($"{what} must be a JSON array");
        }
    }

    /// <summary>Id of a pack or preset for messages, even when the item is invalid.</summary>
    private static string GetLabel(JsonElement item, int number)
    {
        string? id = TryGetString(item, "id");
        if (string.IsNullOrEmpty(id))
        {
            return $"#{number}";
        }

        return Shorten(id);
    }

    /// <summary>A string field of an item that may be invalid, made safe to show: shortened, control
    /// characters replaced by spaces; null when missing or blank.</summary>
    private static string? GetItemText(JsonElement item, string name)
    {
        string? value = TryGetString(item, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (char c in Shorten(value))
        {
            if (char.IsControl(c))
            {
                text.Append(' ');
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static ManifestNote Note(ManifestNoteCode code, string message)
    {
        return new ManifestNote(code, ManifestSkipReason.None, null, null, message);
    }

    private static string Shorten(string value)
    {
        const int MaxLength = 64;
        if (value.Length <= MaxLength)
        {
            return value;
        }

        return value.Substring(0, MaxLength) + "...";
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (!RelativePathPattern().IsMatch(path))
        {
            return false;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TextureNamePattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^assets/([0-9a-f]{64})\\.png\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathPattern();

    [GeneratedRegex("^[A-Za-z0-9_.]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IniNamePattern();

    private sealed class FormatError : Exception
    {
        public FormatError(string message)
            : this(message, ManifestSkipReason.InvalidValue)
        {
        }

        public FormatError(string message, ManifestSkipReason reason)
            : base(message)
        {
            Reason = reason;
        }

        /// <summary>Why the item is skipped, when the error skips one item instead of the whole manifest.</summary>
        public ManifestSkipReason Reason { get; }
    }
}
