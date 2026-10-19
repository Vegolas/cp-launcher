using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Operations;
using L2Mod.Core.Recipes;

namespace L2Mod.Core.Manifests;

/// <summary>
/// Strict reader of manifest.json (schema 1); the bytes come from the network, so everything is checked.
/// Problems with the envelope (schema, version, enabled, launcher, supportedOriginals, packs array) reject the
/// whole manifest. A pack with an unknown op, an unknown field in an operation, a file outside the whitelist,
/// bad values or too many operations is skipped with a message; so is a preset with a forbidden key
/// (GamePlayViewport*) or an announcement with bad values. Unknown top-level and pack fields are ignored,
/// so later additions do not break older launchers. A manifest with another schema yields only the
/// envelope (enabled, launcher) and no packs or presets.
/// </summary>
public static partial class ManifestParser
{
    public const int SupportedSchema = 1;
    public const string ForbiddenPresetKeyPrefix = "GamePlayViewport";

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
        var messages = new List<string>();
        if (schema != SupportedSchema)
        {
            return ReadEnvelopeOnly(root, schema, messages);
        }

        string version = GetRequiredString(root, "version", 64);
        if (!VersionPattern().IsMatch(version))
        {
            throw new FormatError($"version '{version}' has invalid characters");
        }

        bool enabled = GetRequiredBool(root, "enabled");
        LauncherRelease? launcher = ReadLauncher(root);
        IReadOnlyDictionary<string, IReadOnlyList<string>> originals = ReadSupportedOriginals(root, messages);
        IReadOnlyList<Announcement> announcements = ReadAnnouncements(root, limits, messages);
        IReadOnlyList<Pack> packs = ReadPacks(root, limits, messages);
        IReadOnlyList<OptionPreset> presets = ReadPresets(root, limits, messages);
        var manifest = new Manifest(schema, version, enabled, launcher, originals, announcements, packs, presets);
        return new ManifestReadResult(manifest, messages);
    }

    private static ManifestReadResult ReadEnvelopeOnly(JsonElement root, int schema, List<string> messages)
    {
        messages.Add($"Manifest schema {schema} is not supported by this launcher (it reads schema {SupportedSchema}); packs and presets are skipped. Update the launcher.");
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
            messages.Add($"Launcher entry ignored: {error.Message}.");
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
        return new ManifestReadResult(manifest, messages);
    }

    private static LauncherRelease? ReadLauncher(JsonElement root)
    {
        if (!root.TryGetProperty("launcher", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(element, "launcher");
        string version = GetRequiredString(element, "version", 32);
        if (!LauncherVersionPattern().IsMatch(version))
        {
            throw new FormatError($"launcher version '{version}' must look like 1.2.3");
        }

        string url = GetRequiredString(element, "url", 256);
        if (!IsSafeRelativePath(url))
        {
            throw new FormatError($"launcher url '{url}' must be a relative path without '..'");
        }

        return new LauncherRelease(version, url, GetRequiredSha256(element, "sha256"));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSupportedOriginals(JsonElement root, List<string> messages)
    {
        JsonElement element = GetRequiredProperty(root, "supportedOriginals");
        RequireObject(element, "supportedOriginals");
        var originals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!ClientFiles.IsPatchable(property.Name))
            {
                messages.Add($"supportedOriginals: '{Shorten(property.Name)}' is not a patchable file; ignored.");
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

    private static IReadOnlyList<Announcement> ReadAnnouncements(JsonElement root, ManifestLimits limits, List<string> messages)
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
                messages.Add($"Only the first {limits.MaxAnnouncements} announcements are shown.");
                break;
            }

            try
            {
                announcements.Add(ReadAnnouncement(item));
            }
            catch (FormatError error)
            {
                messages.Add($"Announcement {number} skipped: {error.Message}.");
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

    private static IReadOnlyList<Pack> ReadPacks(JsonElement root, ManifestLimits limits, List<string> messages)
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
                messages.Add($"Only the first {limits.MaxPacks} packs are used; the rest are skipped.");
                break;
            }

            string label = GetLabel(item, number);
            try
            {
                Pack pack = ReadPack(item, limits);
                if (ids.Contains(pack.Id))
                {
                    throw new FormatError("another pack has the same id");
                }

                if (totalOperations + pack.Operations.Count > limits.MaxOperationsTotal)
                {
                    throw new FormatError($"the manifest would exceed {limits.MaxOperationsTotal} operations in total");
                }

                ids.Add(pack.Id);
                totalOperations += pack.Operations.Count;
                packs.Add(pack);
            }
            catch (FormatError error)
            {
                messages.Add($"Pack '{label}' skipped: {error.Message}.");
            }
        }

        return packs;
    }

    private static Pack ReadPack(JsonElement item, ManifestLimits limits)
    {
        RequireObject(item, "a pack");
        string id = ReadId(item);
        string name = GetRequiredText(item, "name", MaxNameLength, false);
        bool isDefault = GetOptionalBool(item, "default", false);
        JsonElement operationsElement = GetRequiredProperty(item, "operations");
        RequireArray(operationsElement, "operations");
        int count = operationsElement.GetArrayLength();
        if (count == 0)
        {
            throw new FormatError("it has no operations");
        }

        if (count > limits.MaxOperationsPerPack)
        {
            throw new FormatError($"it has {count} operations; the limit is {limits.MaxOperationsPerPack}");
        }

        var operations = new List<IIconOperation>();
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
                throw new FormatError($"operation {number}: {error.Message}");
            }
        }

        return new Pack(id, name, isDefault, operations);
    }

    private static IIconOperation ReadOperation(JsonElement element)
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
            default:
                throw new FormatError($"unknown op '{Shorten(kind)}' (this launcher knows {string.Join(", ", OperationKinds.All)})");
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new FormatError($"unknown field '{Shorten(property.Name)}' in a {kind} operation");
            }
        }

        string file = GetRequiredString(element, "file", 64);
        if (!ClientFiles.IsPatchable(file))
        {
            throw new FormatError($"file '{Shorten(file)}' is not allowed (allowed: {string.Join(", ", ClientFiles.Patchable)})");
        }

        file = ClientFiles.GetCanonicalName(file);
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

    private static IReadOnlyList<OptionPreset> ReadPresets(JsonElement root, ManifestLimits limits, List<string> messages)
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
                messages.Add($"Only the first {limits.MaxPresets} presets are used; the rest are skipped.");
                break;
            }

            string label = GetLabel(item, number);
            try
            {
                OptionPreset preset = ReadPreset(item, limits);
                if (!ids.Add(preset.Id))
                {
                    throw new FormatError("another preset has the same id");
                }

                presets.Add(preset);
            }
            catch (FormatError error)
            {
                messages.Add($"Preset '{label}' skipped: {error.Message}.");
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
                    throw new FormatError($"it sets {qualified}; window position keys ({ForbiddenPresetKeyPrefix}*) are not allowed");
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
            throw new FormatError($"it sets {values.Count} values; the limit is {limits.MaxPresetValues}");
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

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LauncherVersionPattern();

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
            : base(message)
        {
        }
    }
}
