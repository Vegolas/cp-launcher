using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Publishing;

/// <summary>
/// Writes manifest.json (schema 1) in the form <see cref="ManifestParser"/> reads: UTF-8 without BOM, 2-space
/// indentation, LF line endings, a fixed property order and every operation field spelled out. The output is
/// deterministic, so the same recipes give the same bytes and therefore the same signature input.
/// </summary>
public static class ManifestWriter
{
    private static readonly JsonWriterOptions Options = new JsonWriterOptions
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <remarks>
    /// A pack's <see cref="Pack.Description"/> is written as "description". Launchers that predate it ignore unknown
    /// pack fields, so it is safe to publish to them. A pack with <see cref="Pack.Options"/> is written with
    /// "options" instead of "operations"; launchers before 1.2.0 skip it (and effect operations) with a note.
    /// </remarks>
    public static byte[] Write(Manifest manifest)
    {
        if (manifest.Schema != ManifestParser.SupportedSchema)
        {
            throw new ArgumentException($"Only schema {ManifestParser.SupportedSchema} manifests can be written, not {manifest.Schema}.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", manifest.Schema);
            writer.WriteString("version", manifest.Version);
            writer.WriteBoolean("enabled", manifest.Enabled);
            if (manifest.Launcher is not null)
            {
                WriteLauncher(writer, manifest.Launcher);
            }

            WriteSupportedOriginals(writer, manifest.SupportedOriginals);
            WriteAnnouncements(writer, manifest.Announcements);
            WritePacks(writer, manifest.Packs);
            WritePresets(writer, manifest.OptionPresets);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>
    /// Writes the manifest and reads it back with <see cref="ManifestParser"/>. Throws when the launcher would
    /// skip anything (a pack, preset or announcement) or would not read back exactly what was written.
    /// </summary>
    public static byte[] WriteChecked(Manifest manifest)
    {
        byte[] json = Write(manifest);
        ManifestReadResult result = ManifestParser.Parse(json);
        if (result.Messages.Count > 0)
        {
            throw new ManifestException("The launcher would not use the whole manifest: " + string.Join(" ", result.Messages));
        }

        byte[] reread = Write(result.Manifest);
        if (!reread.AsSpan().SequenceEqual(json))
        {
            throw new ManifestException("The manifest does not read back as written (check letter case of hashes and names).");
        }

        return json;
    }

    private static void WriteLauncher(Utf8JsonWriter writer, LauncherRelease launcher)
    {
        writer.WriteStartObject("launcher");
        writer.WriteString("version", launcher.Version);
        writer.WriteString("url", launcher.Url);
        writer.WriteString("sha256", launcher.Sha256);
        writer.WriteEndObject();
    }

    private static void WriteSupportedOriginals(Utf8JsonWriter writer, IReadOnlyDictionary<string, IReadOnlyList<string>> originals)
    {
        writer.WriteStartObject("supportedOriginals");
        foreach (string file in originals.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            writer.WriteStartArray(file);
            foreach (string hash in originals[file])
            {
                writer.WriteStringValue(hash);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteAnnouncements(Utf8JsonWriter writer, IReadOnlyList<Announcement> announcements)
    {
        writer.WriteStartArray("announcements");
        foreach (Announcement announcement in announcements)
        {
            writer.WriteStartObject();
            writer.WriteString("date", announcement.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("title", announcement.Title);
            writer.WriteString("text", announcement.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WritePacks(Utf8JsonWriter writer, IReadOnlyList<Pack> packs)
    {
        writer.WriteStartArray("packs");
        foreach (Pack pack in packs)
        {
            writer.WriteStartObject();
            writer.WriteString("id", pack.Id);
            writer.WriteString("name", pack.Name);
            if (pack.Description is not null)
            {
                writer.WriteString("description", pack.Description);
            }

            writer.WriteBoolean("default", pack.Default);
            if (pack.HasOptions)
            {
                writer.WriteStartArray("options");
                foreach (PackOption option in pack.Options)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", option.Id);
                    writer.WriteString("name", option.Name);
                    if (option.Description is not null)
                    {
                        writer.WriteString("description", option.Description);
                    }

                    writer.WriteBoolean("default", option.Default);
                    WriteOperations(writer, option.Operations);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
            else
            {
                WriteOperations(writer, pack.Operations);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOperations(Utf8JsonWriter writer, IReadOnlyList<IPatchOperation> operations)
    {
        writer.WriteStartArray("operations");
        foreach (IPatchOperation operation in operations)
        {
            WriteOperation(writer, operation);
        }

        writer.WriteEndArray();
    }

    private static void WriteOperation(Utf8JsonWriter writer, IPatchOperation operation)
    {
        writer.WriteStartObject();
        switch (operation)
        {
            case EffectSwapOperation swap:
            {
                writer.WriteString("op", swap.Kind);
                writer.WriteString("file", swap.File);
                writer.WriteStartArray("skills");
                writer.WriteNumberValue(swap.First);
                writer.WriteNumberValue(swap.Second);
                writer.WriteEndArray();
                break;
            }

            case GoldClickArrowOperation arrow:
            {
                writer.WriteString("op", arrow.Kind);
                writer.WriteString("file", arrow.File);
                break;
            }

            case TargetIconOperation icon:
            {
                writer.WriteString("op", icon.Kind);
                writer.WriteString("file", icon.File);
                writer.WriteNumber("skill", icon.Skill);
                writer.WriteNumber("effectClass", icon.EffectClass);
                writer.WriteNumber("seconds", icon.Seconds);
                break;
            }

            case BadgeOperation badge:
            {
                if (badge.Style.Border != 1 || badge.Style.Crop != 0 || badge.Style.BorderColor != Rgb.Black)
                {
                    throw new ArgumentException($"'{badge.Describe()}': a manifest badge always has a 1 px black border and no crop.");
                }

                WriteCommon(writer, badge);
                writer.WriteString("badge", badge.Badge);
                writer.WriteNumber("size", badge.Style.Size);
                writer.WriteString("corner", IconCorners.ToCode(badge.Style.Corner));
                break;
            }

            case TagOperation tag:
            {
                WriteCommon(writer, tag);
                writer.WriteString("color", tag.Color.ToString());
                writer.WriteNumber("size", tag.Size);
                writer.WriteString("corner", IconCorners.ToCode(tag.Corner));
                break;
            }

            case OverlayOperation overlay:
            {
                WriteCommon(writer, overlay);
                writer.WriteString("image", overlay.Image);
                writer.WriteString("imageSha256", overlay.ImageSha256);
                writer.WriteString("corner", IconCorners.ToCode(overlay.Corner));
                break;
            }

            default:
            {
                throw new ArgumentException($"Operation '{operation.Kind}' cannot be written to a manifest.");
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteCommon(Utf8JsonWriter writer, IIconOperation operation)
    {
        writer.WriteString("op", operation.Kind);
        writer.WriteString("file", operation.File);
        writer.WriteString("target", operation.Target);
    }

    /// <summary>Values are grouped by section (first spelling and first appearance win), as the parser reads them.</summary>
    private static void WritePresets(Utf8JsonWriter writer, IReadOnlyList<OptionPreset> presets)
    {
        writer.WriteStartArray("optionPresets");
        foreach (OptionPreset preset in presets)
        {
            writer.WriteStartObject();
            writer.WriteString("id", preset.Id);
            writer.WriteString("name", preset.Name);
            writer.WriteStartObject("values");
            var sections = new List<(string Name, List<IniValue> Values)>();
            var sectionIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IniValue value in preset.Values)
            {
                if (!sectionIndex.TryGetValue(value.Section, out int index))
                {
                    index = sections.Count;
                    sectionIndex[value.Section] = index;
                    sections.Add((value.Section, new List<IniValue>()));
                }

                sections[index].Values.Add(value);
            }

            foreach ((string name, List<IniValue> values) in sections)
            {
                writer.WriteStartObject(name);
                foreach (IniValue value in values)
                {
                    writer.WriteString(value.Key, value.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
