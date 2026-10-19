using System.Text;
using System.Text.Json.Nodes;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests;

public class ManifestParserTests
{
    public const string OriginalHash = "11f1bc1e814bc80773f1f9ea5290003c9dcc6f9396949d522ccacf8409efc87e";
    public static readonly string ImageHash = new string('a', 64);

    /// <summary>The example from the plan (cp-launcher-v1-plan.md), with real-looking hashes.</summary>
    public static string ExampleJson
    {
        get
        {
            return """
                {
                  "schema": 1,
                  "version": "2026.10.20.1",
                  "enabled": true,
                  "launcher": { "version": "1.0.0", "url": "launcher/CpLauncher-1.0.0.exe", "sha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" },
                  "supportedOriginals": { "Icon.utx": ["ORIGINAL"] },
                  "announcements": [ { "date": "2026-10-20", "title": "Start", "text": "Zwykły tekst, bez HTML.\nDruga linia." } ],
                  "packs": [
                    { "id": "dance-song-badges", "name": "Znaczki tańców i pieśni", "default": true,
                      "operations": [ { "op": "badge", "file": "Icon.utx", "target": "skill0365", "badge": "skill1303", "size": 20, "corner": "br" } ] },
                    { "id": "debuff-tags", "name": "Tagi debuffów", "default": true,
                      "operations": [ { "op": "tag", "file": "Icon.utx", "target": "skill0102", "color": "#E02020", "size": 8, "corner": "tl" } ] },
                    { "id": "item-marks", "name": "Oznaczenia przedmiotów", "default": false,
                      "operations": [ { "op": "overlay", "file": "Icon.utx", "target": "etc_i.etc_xxx", "image": "assets/IMAGE.png", "imageSha256": "IMAGE", "corner": "br" } ] }
                  ],
                  "optionPresets": [ { "id": "mass-pvp", "name": "Masowe PvP", "values": { "Video": { "EffectDetail": "0" } } } ]
                }
                """.Replace("ORIGINAL", OriginalHash).Replace("IMAGE", ImageHash);
        }
    }

    [Fact]
    public void Parse_ReadsThePlanExample()
    {
        ManifestReadResult result = ManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleJson));

        Manifest manifest = result.Manifest;
        Assert.Empty(result.Messages);
        Assert.Equal(1, manifest.Schema);
        Assert.Equal("2026.10.20.1", manifest.Version);
        Assert.True(manifest.Enabled);
        Assert.Equal(new LauncherRelease("1.0.0", "launcher/CpLauncher-1.0.0.exe", new string('b', 64)), manifest.Launcher);
        Assert.True(manifest.IsSupportedOriginal("Icon.utx", OriginalHash.ToUpperInvariant()));
        Assert.False(manifest.IsSupportedOriginal("Icon.utx", ImageHash));
        Assert.Equal(new DateOnly(2026, 10, 20), manifest.Announcements[0].Date);
        Assert.Equal(new[] { "dance-song-badges", "debuff-tags", "item-marks" }, manifest.Packs.Select(pack => pack.Id));
        BadgeOperation badge = Assert.IsType<BadgeOperation>(manifest.Packs[0].Operations[0]);
        Assert.Equal(("Icon.utx", "skill0365", "skill1303"), (badge.File, badge.Target, badge.Badge));
        Assert.Equal((20, IconCorner.BottomRight, 1), (badge.Style.Size, badge.Style.Corner, badge.Style.Border));
        Assert.Equal(new TagOperation("Icon.utx", "skill0102", Rgb.ParseHex("#E02020"), 8, IconCorner.TopLeft), manifest.Packs[1].Operations[0]);
        Assert.Equal(new OverlayOperation("Icon.utx", "etc_i.etc_xxx", $"assets/{ImageHash}.png", ImageHash, IconCorner.BottomRight), manifest.Packs[2].Operations[0]);
        Assert.False(manifest.Packs[2].Default);
        Assert.Equal(new[] { new IniValue("Video", "EffectDetail", "0") }, manifest.OptionPresets[0].Values);
    }

    [Fact]
    public void Parse_AppliesOperationDefaults()
    {
        JsonObject root = Example();
        Operation(root, 0).Remove("size");
        Operation(root, 0).Remove("corner");
        Operation(root, 1).Remove("size");
        Operation(root, 1).Remove("corner");

        Manifest manifest = Parse(root).Manifest;

        BadgeOperation badge = Assert.IsType<BadgeOperation>(manifest.Packs[0].Operations[0]);
        Assert.Equal((20, IconCorner.BottomRight), (badge.Style.Size, badge.Style.Corner));
        TagOperation tag = Assert.IsType<TagOperation>(manifest.Packs[1].Operations[0]);
        Assert.Equal((8, IconCorner.TopLeft), (tag.Size, tag.Corner));
    }

    [Fact]
    public void Parse_SkipsPackWithUnknownOpAndKeepsTheOthers()
    {
        JsonObject root = Example();
        Operation(root, 1)["op"] = "recolor";

        ManifestReadResult result = Parse(root);

        Assert.Equal(new[] { "dance-song-badges", "item-marks" }, result.Manifest.Packs.Select(pack => pack.Id));
        string message = Assert.Single(result.Messages);
        Assert.Contains("'debuff-tags' skipped", message);
        Assert.Contains("unknown op 'recolor'", message);
    }

    [Theory]
    [InlineData("Icon_c.utx")]
    [InlineData("Interface.utx")]
    [InlineData("..\\system_season\\L2.ini")]
    public void Parse_SkipsPackWithFileOutsideTheWhitelist(string file)
    {
        JsonObject root = Example();
        Operation(root, 0)["file"] = file;

        ManifestReadResult result = Parse(root);

        Assert.DoesNotContain(result.Manifest.Packs, pack => pack.Id == "dance-song-badges");
        Assert.Contains(result.Messages, message => message.Contains("is not allowed"));
    }

    [Fact]
    public void Parse_AcceptsWhitelistedFileInAnyCaseAndStoresTheCanonicalName()
    {
        JsonObject root = Example();
        Operation(root, 1)["file"] = "icon.UTX";

        ManifestReadResult result = Parse(root);

        Assert.Empty(result.Messages);
        Assert.Equal("Icon.utx", result.Manifest.Packs[1].Operations[0].File);
    }

    [Theory]
    [InlineData(0, "border", 2)]
    [InlineData(1, "edge", 1)]
    [InlineData(2, "scale", 2)]
    public void Parse_SkipsPackWithUnknownOperationField(int pack, string field, int value)
    {
        JsonObject root = Example();
        Operation(root, pack)[field] = value;

        ManifestReadResult result = Parse(root);

        Assert.Equal(2, result.Manifest.Packs.Count);
        Assert.Contains(result.Messages, message => message.Contains($"unknown field '{field}'"));
    }

    [Theory]
    [InlineData(0, "size", 36)]
    [InlineData(0, "size", 18)]
    [InlineData(1, "size", 6)]
    [InlineData(1, "size", 20)]
    [InlineData(1, "corner", "middle")]
    [InlineData(1, "color", "E02020")]
    [InlineData(1, "target", "skill 0102")]
    public void Parse_SkipsPackWithInvalidValues(int pack, string field, object value)
    {
        JsonObject root = Example();
        if (value is int number)
        {
            Operation(root, pack)[field] = number;
        }
        else
        {
            Operation(root, pack)[field] = (string)value;
        }

        ManifestReadResult result = Parse(root);

        Assert.Equal(2, result.Manifest.Packs.Count);
        Assert.Single(result.Messages);
    }

    [Fact]
    public void Parse_SkipsOverlayWhoseHashDoesNotMatchTheImageName()
    {
        JsonObject root = Example();
        Operation(root, 2)["imageSha256"] = new string('c', 64);

        ManifestReadResult result = Parse(root);

        Assert.DoesNotContain(result.Manifest.Packs, pack => pack.Id == "item-marks");
        Assert.Contains(result.Messages, message => message.Contains("imageSha256"));
    }

    [Fact]
    public void Parse_SkipsPackOverTheOperationLimit()
    {
        JsonObject root = Example();
        JsonArray operations = Operations(root, 1);
        operations.Add(operations[0]!.DeepClone());
        operations.Add(operations[0]!.DeepClone());

        ManifestReadResult result = Parse(root, ManifestLimits.Default with { MaxOperationsPerPack = 2 });

        Assert.DoesNotContain(result.Manifest.Packs, pack => pack.Id == "debuff-tags");
        Assert.Contains(result.Messages, message => message.Contains("3 operations; the limit is 2"));
    }

    [Fact]
    public void Parse_SkipsPacksOverTheTotalOperationLimit()
    {
        ManifestReadResult result = Parse(Example(), ManifestLimits.Default with { MaxOperationsTotal = 2 });

        Assert.Equal(2, result.Manifest.Packs.Count);
        Assert.Contains(result.Messages, message => message.Contains("'item-marks' skipped") && message.Contains("in total"));
    }

    [Fact]
    public void Parse_SkipsPackWithDuplicateId()
    {
        JsonObject root = Example();
        Pack(root, 2)["id"] = "debuff-tags";

        ManifestReadResult result = Parse(root);

        Assert.Equal(new[] { "dance-song-badges", "debuff-tags" }, result.Manifest.Packs.Select(pack => pack.Id));
        Assert.Contains(result.Messages, message => message.Contains("same id"));
    }

    [Theory]
    [InlineData("GamePlayViewportX")]
    [InlineData("gameplayviewportstarty")]
    public void Parse_SkipsPresetWithWindowPositionKeys(string key)
    {
        JsonObject root = Example();
        JsonObject second = root["optionPresets"]![0]!.DeepClone().AsObject();
        second["id"] = "low-effects";
        root["optionPresets"]!.AsArray().Add(second);
        PresetValues(root)["Video"]![key] = "100";

        ManifestReadResult result = Parse(root);

        Assert.Equal(new[] { "low-effects" }, result.Manifest.OptionPresets.Select(preset => preset.Id));
        Assert.Contains(result.Messages, message => message.Contains("'mass-pvp' skipped") && message.Contains("GamePlayViewport"));
        Assert.Equal(3, result.Manifest.Packs.Count);
    }

    [Theory]
    [InlineData("0\r\n[Evil]")]
    [InlineData("0\u0000")]
    public void Parse_SkipsPresetWithControlCharactersInValues(string value)
    {
        JsonObject root = Example();
        PresetValues(root)["Video"]!["EffectDetail"] = value;

        ManifestReadResult result = Parse(root);

        Assert.Empty(result.Manifest.OptionPresets);
        Assert.Single(result.Messages);
    }

    [Fact]
    public void Parse_SkipsPresetWithNonStringValue()
    {
        JsonObject root = Example();
        PresetValues(root)["Video"]!["EffectDetail"] = 0;

        ManifestReadResult result = Parse(root);

        Assert.Empty(result.Manifest.OptionPresets);
    }

    [Fact]
    public void Parse_UnknownSchemaKeepsOnlyTheEnvelope()
    {
        JsonObject root = Example();
        root["schema"] = 2;
        root["enabled"] = false;
        root["packs"] = "something new";

        ManifestReadResult result = Parse(root);

        Assert.Equal(2, result.Manifest.Schema);
        Assert.False(result.Manifest.Enabled);
        Assert.NotNull(result.Manifest.Launcher);
        Assert.Empty(result.Manifest.Packs);
        Assert.Empty(result.Manifest.OptionPresets);
        Assert.Contains("schema 2", Assert.Single(result.Messages));
    }

    [Fact]
    public void Parse_IgnoresUnknownTopLevelFields()
    {
        JsonObject root = Example();
        root["motd"] = "later addition";

        ManifestReadResult result = Parse(root);

        Assert.Empty(result.Messages);
        Assert.Equal(3, result.Manifest.Packs.Count);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("version")]
    [InlineData("supportedOriginals")]
    [InlineData("packs")]
    public void Parse_RejectsManifestWithoutRequiredField(string field)
    {
        JsonObject root = Example();
        root.Remove(field);

        ManifestException exception = Assert.Throws<ManifestException>(() => Parse(root));

        Assert.Contains(field, exception.Message);
    }

    [Theory]
    [InlineData("launcher/../../evil.exe")]
    [InlineData("https://example.com/CpLauncher.exe")]
    [InlineData("/launcher/CpLauncher.exe")]
    public void Parse_RejectsLauncherUrlOutsideTheBucket(string url)
    {
        JsonObject root = Example();
        root["launcher"]!["url"] = url;

        Assert.Throws<ManifestException>(() => Parse(root));
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.00.0")]
    [InlineData("1.0.01")]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0 ")]
    public void Parse_RejectsLauncherVersionsTheUpdaterRefuses(string version)
    {
        JsonObject root = Example();
        root["launcher"]!["version"] = version;

        ManifestException exception = Assert.Throws<ManifestException>(() => Parse(root));

        Assert.Contains("launcher version", exception.Message);
        Assert.False(LauncherVersion.TryParse(version, out _));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("10.0.0")]
    [InlineData("0.9.0")]
    [InlineData("1.1.0-beta.1")]
    [InlineData("1.0.1+abc123")]
    public void Parse_AcceptsLauncherVersionsTheUpdaterAccepts(string version)
    {
        JsonObject root = Example();
        root["launcher"]!["version"] = version;

        Manifest manifest = Parse(root).Manifest;

        Assert.Equal(version, manifest.Launcher?.Version);
        Assert.True(LauncherVersion.TryParse(version, out _));
    }

    [Fact]
    public void Parse_UnknownSchema_IgnoresALauncherVersionTheUpdaterRefuses()
    {
        JsonObject root = Example();
        root["schema"] = 2;
        root["launcher"]!["version"] = "01.0.0";

        ManifestReadResult result = Parse(root);

        Assert.Null(result.Manifest.Launcher);
        Assert.Equal(new[] { ManifestNoteCode.UnsupportedSchema, ManifestNoteCode.LauncherIgnored }, result.Notes.Select(note => note.Code));
    }

    [Fact]
    public void Notes_CarryCodeReasonAndThePackIdAndName()
    {
        JsonObject root = Example();
        Operation(root, 0)["op"] = "recolor";
        Operation(root, 1)["edge"] = 1;
        Operation(root, 2)["file"] = "Icon_c.utx";

        ManifestReadResult result = Parse(root);

        Assert.Empty(result.Manifest.Packs);
        Assert.Equal(
            new (ManifestNoteCode, ManifestSkipReason, string?, string?)[]
            {
                (ManifestNoteCode.PackSkipped, ManifestSkipReason.UnknownOperation, "dance-song-badges", "Znaczki tańców i pieśni"),
                (ManifestNoteCode.PackSkipped, ManifestSkipReason.UnknownField, "debuff-tags", "Tagi debuffów"),
                (ManifestNoteCode.PackSkipped, ManifestSkipReason.FileNotAllowed, "item-marks", "Oznaczenia przedmiotów"),
            },
            result.Notes.Select(note => (note.Code, note.Reason, note.ItemId, note.ItemName)));
        Assert.Equal(result.Notes.Select(note => note.Message), result.Messages);
        Assert.Contains("unknown op 'recolor'", result.Notes[0].Message);
    }

    [Fact]
    public void Notes_TellLimitsDuplicatesAndBadValuesApart()
    {
        JsonObject root = Example();
        Operation(root, 0)["size"] = 18;
        JsonArray operations = Operations(root, 1);
        operations.Add(operations[0]!.DeepClone());

        ManifestReadResult result = Parse(root, ManifestLimits.Default with { MaxOperationsPerPack = 1 });

        Assert.Equal(new[] { ManifestSkipReason.InvalidValue, ManifestSkipReason.OverLimit }, result.Notes.Select(note => note.Reason));
        Assert.Equal(new[] { ManifestSkipReason.DuplicateId }, Parse(DuplicatePackIds()).Notes.Select(note => note.Reason));
        Assert.Equal(
            new[] { ManifestNoteCode.PackLimit },
            Parse(Example(), ManifestLimits.Default with { MaxPacks = 2 }).Notes.Select(note => note.Code));
    }

    [Fact]
    public void Notes_ForPresetsAnnouncementsAndOriginals()
    {
        JsonObject root = Example();
        PresetValues(root)["Video"]!["GamePlayViewportX"] = "1";
        root["announcements"]![0]!["date"] = "20.10.2026";
        root["supportedOriginals"]!["L2.ini"] = new JsonArray();

        ManifestReadResult result = Parse(root);

        Assert.Equal(
            new (ManifestNoteCode, ManifestSkipReason, string?, string?)[]
            {
                (ManifestNoteCode.OriginalIgnored, ManifestSkipReason.None, null, null),
                (ManifestNoteCode.AnnouncementSkipped, ManifestSkipReason.InvalidValue, null, "Start"),
                (ManifestNoteCode.PresetSkipped, ManifestSkipReason.ForbiddenKey, "mass-pvp", "Masowe PvP"),
            },
            result.Notes.Select(note => (note.Code, note.Reason, note.ItemId, note.ItemName)));
    }

    [Fact]
    public void Notes_ItemTextIsShortenedAndHasNoControlCharacters()
    {
        JsonObject root = Example();
        Operation(root, 0)["op"] = "recolor";
        Pack(root, 0)["name"] = "Zła\nnazwa" + new string('x', 100);
        Pack(root, 0).Remove("id");

        ManifestNote note = Parse(root).Notes.Single();

        Assert.Null(note.ItemId);
        Assert.StartsWith("Zła nazwa", note.ItemName);
        Assert.EndsWith("...", note.ItemName);
        Assert.Equal(67, note.ItemName!.Length);
    }

    [Fact]
    public void Parse_RejectsInvalidJsonDuplicatePropertiesAndOversizedManifests()
    {
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(Encoding.UTF8.GetBytes("{ \"schema\": 1, ")));
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleJson.Replace("\"enabled\": true,", "\"enabled\": true, \"enabled\": false,"))));
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(Encoding.UTF8.GetBytes(ExampleJson), ManifestLimits.Default with { MaxManifestBytes = 100 }));
    }

    [Fact]
    public void GetOperations_FollowsSelectedPacksInManifestOrder()
    {
        Manifest manifest = Parse(Example()).Manifest;

        IReadOnlyList<IIconOperation> operations = manifest.GetOperations("Icon.utx", new[] { "item-marks", "dance-song-badges", "missing" });

        Assert.Equal(new[] { "badge", "overlay" }, operations.Select(operation => operation.Kind));
    }

    private static JsonObject Example()
    {
        return JsonNode.Parse(ExampleJson)!.AsObject();
    }

    private static JsonObject DuplicatePackIds()
    {
        JsonObject root = Example();
        Pack(root, 2)["id"] = "debuff-tags";
        return root;
    }

    private static ManifestReadResult Parse(JsonObject root, ManifestLimits? limits = null)
    {
        return ManifestParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), limits);
    }

    private static JsonObject Pack(JsonObject root, int index)
    {
        return root["packs"]![index]!.AsObject();
    }

    private static JsonArray Operations(JsonObject root, int pack)
    {
        return Pack(root, pack)["operations"]!.AsArray();
    }

    private static JsonObject Operation(JsonObject root, int pack)
    {
        return Operations(root, pack)[0]!.AsObject();
    }

    private static JsonObject PresetValues(JsonObject root)
    {
        return root["optionPresets"]![0]!["values"]!.AsObject();
    }
}
