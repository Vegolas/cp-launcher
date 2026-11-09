using System.Globalization;
using System.Text.Json;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;

namespace L2Mod.Core.Publishing;

public enum RecipePackType
{
    Badge,
    Tag,
    Overlay,

    /// <summary>Skill effect swaps, one option per pair (LineageSkillEffect.u).</summary>
    EffectSwap,

    /// <summary>The gold click-to-move arrow, no mapping (LineageEffect.u).</summary>
    GoldClickArrow,

    /// <summary>Icons over the target, one option per skill (LineageSkillEffect.u).</summary>
    TargetIcon,
}

/// <summary>
/// One pack of recipes.json. <see cref="MappingPath"/> is absolute (empty for a type without mapping);
/// <see cref="Size"/>, <see cref="Corner"/> and <see cref="Colors"/> are null when the approved defaults apply (see
/// <see cref="Recipes.RecipeDefaults"/>).
/// </summary>
public sealed record RecipePack(
    string Id,
    string Name,
    string? Description,
    bool Default,
    RecipePackType Type,
    string MappingPath,
    int? Size,
    IconCorner? Corner,
    IReadOnlyDictionary<string, Rgb>? Colors)
{
    /// <summary>Whether the pack changes icons in Icon.utx (and so has a before/after preview sheet).</summary>
    public bool IsIconPack
    {
        get
        {
            return Type is RecipePackType.Badge or RecipePackType.Tag or RecipePackType.Overlay;
        }
    }

    /// <summary>The manifest name of a type, as written in recipes.json.</summary>
    public static string GetTypeName(RecipePackType type)
    {
        switch (type)
        {
            case RecipePackType.Badge:
                return "badge";
            case RecipePackType.Tag:
                return "tag";
            case RecipePackType.Overlay:
                return "overlay";
            case RecipePackType.EffectSwap:
                return "effect-swap";
            case RecipePackType.GoldClickArrow:
                return "gold-click-arrow";
            default:
                return "target-icon";
        }
    }
}

/// <summary>A launcher release to publish with the manifest; <see cref="FilePath"/> is absolute.</summary>
public sealed record RecipeLauncher(string Version, string FilePath);

/// <summary>
/// The leader's authoring folder: recipes.json (format 1) plus the mapping files and PNGs it references, with
/// paths relative to the folder of recipes.json. Comments and trailing commas are allowed. Unknown fields are
/// errors, so a typo cannot silently drop a setting. What the launcher checks itself (ids, text lengths,
/// preset keys) is validated when the manifest is written and read back (<see cref="ManifestWriter.WriteChecked"/>).
/// The format is documented in recipes/README.md.
/// </summary>
public sealed record RecipeProject(
    string BaseDirectory,
    string Version,
    bool Enabled,
    RecipeLauncher? Launcher,
    IReadOnlyList<RecipePack> Packs,
    IReadOnlyList<Announcement> Announcements,
    IReadOnlyList<OptionPreset> OptionPresets)
{
    public const string FileName = "recipes.json";
    public const int SupportedFormat = 1;
    public const int MaxDescriptionLength = ManifestParser.MaxDescriptionLength;

    private static readonly string[] RootFields = { "format", "version", "enabled", "launcher", "packs", "announcements", "optionPresets" };
    private static readonly string[] LauncherFields = { "version", "file" };
    private static readonly string[] PackFields = { "id", "name", "description", "default", "type", "mapping", "size", "corner", "colors" };
    private static readonly string[] AnnouncementFields = { "date", "title", "text" };
    private static readonly string[] PresetFields = { "id", "name", "values" };

    /// <param name="directory">The recipes folder (containing recipes.json) or the path of recipes.json itself.</param>
    public static RecipeProject Load(string directory)
    {
        string path = Path.GetFullPath(directory);
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, FileName);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} not found.", path);
        }

        return Parse(File.ReadAllBytes(path), Path.GetDirectoryName(path)!);
    }

    public static RecipeProject Parse(byte[] json, string baseDirectory)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowDuplicateProperties = false,
        };
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, options);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{FileName} is not valid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            return ParseRoot(document.RootElement, Path.GetFullPath(baseDirectory));
        }
    }

    private static RecipeProject ParseRoot(JsonElement root, string baseDirectory)
    {
        RequireObject(root, "the root");
        CheckFields(root, RootFields, "the root");
        int format = GetRequiredInt(root, "format", "format");
        if (format != SupportedFormat)
        {
            throw Error("format", $"must be {SupportedFormat}, not {format}");
        }

        string version = GetRequiredString(root, "version", "version");
        bool enabled = GetOptionalBool(root, "enabled", "enabled", true);
        RecipeLauncher? launcher = null;
        if (root.TryGetProperty("launcher", out JsonElement launcherElement))
        {
            RequireObject(launcherElement, "launcher");
            CheckFields(launcherElement, LauncherFields, "launcher");
            launcher = new RecipeLauncher(
                GetRequiredString(launcherElement, "version", "launcher.version"),
                ResolvePath(baseDirectory, GetRequiredString(launcherElement, "file", "launcher.file")));
        }

        JsonElement packsElement = GetRequired(root, "packs", "packs");
        RequireArray(packsElement, "packs");
        var packs = new List<RecipePack>();
        foreach (JsonElement pack in packsElement.EnumerateArray())
        {
            packs.Add(ParsePack(pack, $"packs[{packs.Count}]", baseDirectory));
        }

        if (packs.Count == 0)
        {
            throw Error("packs", "lists no packs");
        }

        var announcements = new List<Announcement>();
        foreach (JsonElement item in GetOptionalArray(root, "announcements"))
        {
            announcements.Add(ParseAnnouncement(item, $"announcements[{announcements.Count}]"));
        }

        var presets = new List<OptionPreset>();
        foreach (JsonElement item in GetOptionalArray(root, "optionPresets"))
        {
            presets.Add(ParsePreset(item, $"optionPresets[{presets.Count}]"));
        }

        return new RecipeProject(baseDirectory, version, enabled, launcher, packs, announcements, presets);
    }

    private static RecipePack ParsePack(JsonElement element, string where, string baseDirectory)
    {
        RequireObject(element, where);
        CheckFields(element, PackFields, where);
        string typeName = GetRequiredString(element, "type", $"{where}.type");
        RecipePackType? parsedType = null;
        foreach (RecipePackType candidate in Enum.GetValues<RecipePackType>())
        {
            if (RecipePack.GetTypeName(candidate) == typeName)
            {
                parsedType = candidate;
            }
        }

        if (parsedType is not RecipePackType type)
        {
            string known = string.Join(", ", Enum.GetValues<RecipePackType>().Select(RecipePack.GetTypeName));
            throw Error($"{where}.type", $"'{typeName}' is not one of {known}");
        }

        bool iconPack = type is RecipePackType.Badge or RecipePackType.Tag or RecipePackType.Overlay;
        foreach (string iconField in new[] { "size", "corner" })
        {
            if (!iconPack && element.TryGetProperty(iconField, out _))
            {
                throw Error($"{where}.{iconField}", $"only icon packs (badge, tag, overlay) have a {iconField}");
            }
        }

        string mappingPath = string.Empty;
        if (type == RecipePackType.GoldClickArrow)
        {
            if (element.TryGetProperty("mapping", out _))
            {
                throw Error($"{where}.mapping", "a gold-click-arrow pack has no mapping");
            }
        }
        else
        {
            mappingPath = ResolvePath(baseDirectory, GetRequiredString(element, "mapping", $"{where}.mapping"));
        }

        int? size = null;
        if (element.TryGetProperty("size", out _))
        {
            if (type == RecipePackType.Overlay)
            {
                throw Error($"{where}.size", "an overlay has the size of its PNG");
            }

            size = GetRequiredInt(element, "size", $"{where}.size");
        }

        IconCorner? corner = null;
        if (element.TryGetProperty("corner", out _))
        {
            string code = GetRequiredString(element, "corner", $"{where}.corner");
            if (code is not ("br" or "bl" or "tr" or "tl"))
            {
                throw Error($"{where}.corner", "must be br, bl, tr or tl");
            }

            corner = IconCorners.Parse(code);
        }

        IReadOnlyDictionary<string, Rgb>? colors = null;
        if (element.TryGetProperty("colors", out JsonElement colorsElement))
        {
            if (type != RecipePackType.Tag)
            {
                throw Error($"{where}.colors", "only tag packs have category colours");
            }

            colors = ParseColors(colorsElement, $"{where}.colors");
        }

        string? description = null;
        if (element.TryGetProperty("description", out _))
        {
            description = GetText(element, "description", $"{where}.description");
            if (description.Length > MaxDescriptionLength || description.Any(c => char.IsControl(c) && c != '\n'))
            {
                throw Error($"{where}.description", $"must have at most {MaxDescriptionLength} characters and no control characters except line breaks");
            }
        }

        return new RecipePack(
            GetRequiredString(element, "id", $"{where}.id"),
            GetRequiredString(element, "name", $"{where}.name"),
            description,
            GetOptionalBool(element, "default", $"{where}.default", false),
            type,
            mappingPath,
            size,
            corner,
            colors);
    }

    private static IReadOnlyDictionary<string, Rgb> ParseColors(JsonElement element, string where)
    {
        RequireObject(element, where);
        var colors = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? value = null;
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
            }

            if (value is null || value.Length != 7 || value[0] != '#')
            {
                throw Error($"{where}.{property.Name}", "must be a #RRGGBB string");
            }

            try
            {
                colors.Add(property.Name, Rgb.ParseHex(value));
            }
            catch (ArgumentException exception)
            {
                throw Error($"{where}.{property.Name}", exception.Message);
            }
        }

        if (colors.Count == 0)
        {
            throw Error(where, "lists no categories");
        }

        return colors;
    }

    private static Announcement ParseAnnouncement(JsonElement element, string where)
    {
        RequireObject(element, where);
        CheckFields(element, AnnouncementFields, where);
        string date = GetRequiredString(element, "date", $"{where}.date");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            throw Error($"{where}.date", $"'{date}' must be yyyy-MM-dd");
        }

        return new Announcement(parsed, GetRequiredString(element, "title", $"{where}.title"), GetText(element, "text", $"{where}.text"));
    }

    private static OptionPreset ParsePreset(JsonElement element, string where)
    {
        RequireObject(element, where);
        CheckFields(element, PresetFields, where);
        JsonElement sections = GetRequired(element, "values", $"{where}.values");
        RequireObject(sections, $"{where}.values");
        var values = new List<IniValue>();
        foreach (JsonProperty section in sections.EnumerateObject())
        {
            string sectionWhere = $"{where}.values.{section.Name}";
            RequireObject(section.Value, sectionWhere);
            foreach (JsonProperty key in section.Value.EnumerateObject())
            {
                string value;
                if (key.Value.ValueKind == JsonValueKind.String)
                {
                    value = key.Value.GetString()!;
                }
                else if (key.Value.ValueKind == JsonValueKind.Number)
                {
                    value = key.Value.GetRawText();
                }
                else
                {
                    throw Error($"{sectionWhere}.{key.Name}", "must be a string or a number");
                }

                values.Add(new IniValue(section.Name, key.Name, value));
            }
        }

        return new OptionPreset(GetRequiredString(element, "id", $"{where}.id"), GetRequiredString(element, "name", $"{where}.name"), values);
    }

    /// <summary>A string, or an array of strings joined with "\n" (multi-line texts without escapes).</summary>
    private static string GetText(JsonElement element, string name, string where)
    {
        JsonElement value = GetRequired(element, name, where);
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error(where, "must be a string or an array of lines");
        }

        var lines = new List<string>();
        foreach (JsonElement line in value.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.String)
            {
                throw Error(where, "must be a string or an array of lines");
            }

            lines.Add(line.GetString()!);
        }

        return string.Join("\n", lines);
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static void CheckFields(JsonElement element, string[] known, string where)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Error(where, $"unknown field '{property.Name}' (known: {string.Join(", ", known)})");
            }
        }
    }

    private static IEnumerable<JsonElement> GetOptionalArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return Array.Empty<JsonElement>();
        }

        RequireArray(value, name);
        return value.EnumerateArray().ToList();
    }

    private static JsonElement GetRequired(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            throw Error(where, "is missing");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement element, string name, string where)
    {
        JsonElement value = GetRequired(element, name, where);
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length == 0)
        {
            throw Error(where, "must be a non-empty string");
        }

        return value.GetString()!;
    }

    private static int GetRequiredInt(JsonElement element, string name, string where)
    {
        JsonElement value = GetRequired(element, name, where);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            throw Error(where, "must be an integer");
        }

        return number;
    }

    private static bool GetOptionalBool(JsonElement element, string name, string where, bool defaultValue)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        throw Error(where, "must be true or false");
    }

    private static void RequireObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(where, "must be a JSON object");
        }
    }

    private static void RequireArray(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error(where, "must be a JSON array");
        }
    }

    private static FormatException Error(string where, string message)
    {
        return new FormatException($"{FileName}: {where}: {message}.");
    }
}
