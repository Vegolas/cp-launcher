using System.Text;
using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Publishing;

namespace L2Mod.Core.Tests.Publishing;

public class ManifestWriterTests
{
    private static readonly string OriginalHash = ManifestParserTests.OriginalHash;

    [Fact]
    public void Write_PlanExample_ReadsBackWithTheSameContent()
    {
        Manifest example = ManifestParser.Parse(Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson)).Manifest;

        byte[] json = ManifestWriter.Write(example);
        ManifestReadResult reread = ManifestParser.Parse(json);

        Assert.Empty(reread.Messages);
        Manifest manifest = reread.Manifest;
        Assert.Equal((example.Schema, example.Version, example.Enabled, example.Launcher), (manifest.Schema, manifest.Version, manifest.Enabled, manifest.Launcher));
        Assert.Equal(example.SupportedOriginals["Icon.utx"], manifest.SupportedOriginals["Icon.utx"]);
        Assert.Equal(example.Announcements, manifest.Announcements);
        Assert.Equal(example.Packs.Select(pack => (pack.Id, pack.Name, pack.Default)), manifest.Packs.Select(pack => (pack.Id, pack.Name, pack.Default)));
        Assert.Equal(example.Packs.SelectMany(pack => pack.Operations), manifest.Packs.SelectMany(pack => pack.Operations));
        Assert.Equal(example.OptionPresets.Single().Values, manifest.OptionPresets.Single().Values);
        Assert.Equal(json, ManifestWriter.Write(manifest));
    }

    [Fact]
    public void Write_IsUtf8WithoutBomWithLfAndReadablePolishText()
    {
        Manifest example = ManifestParser.Parse(Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson)).Manifest;

        byte[] json = ManifestWriter.Write(example);
        string text = Encoding.UTF8.GetString(json);

        Assert.NotEqual(0xEF, json[0]);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"name\": \"Znaczki tańców i pieśni\"", text);
        Assert.Contains("\"text\": \"Zwykły tekst, bez HTML.\\nDruga linia.\"", text);
        Assert.Equal(json, ManifestWriter.Write(example));
    }

    [Fact]
    public void Write_SpellsOutEveryOperationField()
    {
        Manifest manifest = CreateManifest(new Pack("badges", "Badges", true, new IIconOperation[]
        {
            new BadgeOperation("Icon.utx", "skill0365", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 1)),
            new TagOperation("Icon.utx", "skill0102", Rgb.ParseHex("#e02020"), 8, IconCorner.TopLeft),
        }));

        string text = Encoding.UTF8.GetString(ManifestWriter.Write(manifest));

        Assert.Contains("\"op\": \"badge\",\n          \"file\": \"Icon.utx\",\n          \"target\": \"skill0365\",\n          \"badge\": \"skill1303\",\n          \"size\": 20,\n          \"corner\": \"br\"", text);
        Assert.Contains("\"color\": \"#E02020\",\n          \"size\": 8,\n          \"corner\": \"tl\"", text);
    }

    [Fact]
    public void Write_AddsPackDescriptionsThatTheParserIgnores()
    {
        Manifest manifest = CreateManifest(TagPack("tags"));
        var descriptions = new Dictionary<string, string> { ["tags"] = "Kolorowe narożniki.\nDruga linia." };

        byte[] json = ManifestWriter.WriteChecked(manifest, descriptions);

        Assert.Contains("\"description\": \"Kolorowe narożniki.\\nDruga linia.\"", Encoding.UTF8.GetString(json));
        Assert.Empty(ManifestParser.Parse(json).Messages);
    }

    [Fact]
    public void Write_GroupsPresetValuesBySection()
    {
        var values = new[]
        {
            new IniValue("Video", "EffectDetail", "0"),
            new IniValue("Game", "ShowName", "1"),
            new IniValue("video", "TextureDetail", "2"),
        };
        Manifest manifest = CreateManifest(TagPack("tags")) with
        {
            OptionPresets = new[] { new OptionPreset("mass-pvp", "Masowe PvP", values) },
        };

        byte[] json = ManifestWriter.WriteChecked(manifest);

        IReadOnlyList<IniValue> reread = ManifestParser.Parse(json).Manifest.OptionPresets.Single().Values;
        Assert.Equal(new[] { "Video.EffectDetail", "Video.TextureDetail", "Game.ShowName" }, reread.Select(value => $"{value.Section}.{value.Key}"));
    }

    [Fact]
    public void Write_RejectsBadgesTheManifestCannotExpress()
    {
        var thickBorder = new BadgeOperation("Icon.utx", "skill0365", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 2));
        var cropped = new BadgeOperation("Icon.utx", "skill0365", "skill1303", new BadgeStyle(20, IconCorner.BottomRight, 1, 4));

        Assert.Throws<ArgumentException>(() => ManifestWriter.Write(CreateManifest(new Pack("a", "A", true, new IIconOperation[] { thickBorder }))));
        Assert.Throws<ArgumentException>(() => ManifestWriter.Write(CreateManifest(new Pack("a", "A", true, new IIconOperation[] { cropped }))));
    }

    [Fact]
    public void Write_RejectsOtherSchemas()
    {
        Manifest manifest = CreateManifest(TagPack("tags")) with { Schema = 2 };

        Assert.Throws<ArgumentException>(() => ManifestWriter.Write(manifest));
    }

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("")]
    public void WriteChecked_FailsWhenTheLauncherWouldSkipAPack(string id)
    {
        Manifest manifest = CreateManifest(TagPack("tags"), TagPack(id));

        ManifestException exception = Assert.Throws<ManifestException>(() => ManifestWriter.WriteChecked(manifest));

        Assert.Contains("skipped", exception.Message);
    }

    [Fact]
    public void WriteChecked_FailsOnAForbiddenPresetKey()
    {
        Manifest manifest = CreateManifest(TagPack("tags")) with
        {
            OptionPresets = new[] { new OptionPreset("window", "Okno", new[] { new IniValue("Video", "GamePlayViewportX", "0") }) },
        };

        ManifestException exception = Assert.Throws<ManifestException>(() => ManifestWriter.WriteChecked(manifest));

        Assert.Contains("GamePlayViewport", exception.Message);
    }

    [Fact]
    public void WriteChecked_FailsWhenAHashWouldReadBackDifferently()
    {
        Manifest manifest = CreateManifest(TagPack("tags")) with
        {
            SupportedOriginals = new Dictionary<string, IReadOnlyList<string>> { ["Icon.utx"] = new[] { OriginalHash.ToUpperInvariant() } },
        };

        Assert.Throws<ManifestException>(() => ManifestWriter.WriteChecked(manifest));
    }

    [Fact]
    public void WriteChecked_FailsWhenAPackExceedsTheOperationLimit()
    {
        var operations = new List<IIconOperation>();
        for (int i = 0; i < ManifestLimits.Default.MaxOperationsPerPack + 1; i++)
        {
            operations.Add(new TagOperation("Icon.utx", $"skill{i:D4}", Rgb.Black, 8, IconCorner.TopLeft));
        }

        Manifest manifest = CreateManifest(new Pack("big", "Big", true, operations));

        Assert.Throws<ManifestException>(() => ManifestWriter.WriteChecked(manifest));
    }

    public static Manifest CreateManifest(params Pack[] packs)
    {
        return new Manifest(
            1,
            "2026.10.20.1",
            true,
            null,
            new Dictionary<string, IReadOnlyList<string>> { ["Icon.utx"] = new[] { OriginalHash } },
            Array.Empty<Announcement>(),
            packs,
            Array.Empty<OptionPreset>());
    }

    private static Pack TagPack(string id)
    {
        return new Pack(id, "Tagi", true, new IIconOperation[] { new TagOperation("Icon.utx", "skill0102", Rgb.ParseHex("#E02020"), 8, IconCorner.TopLeft) });
    }
}
