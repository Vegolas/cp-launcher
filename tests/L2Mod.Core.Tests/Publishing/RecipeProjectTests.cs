using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Publishing;

namespace L2Mod.Core.Tests.Publishing;

public class RecipeProjectTests
{
    private static readonly string BaseDirectory = Path.Combine(Path.GetTempPath(), "recipes-base");

    [Fact]
    public void Parse_ReadsTheFixture()
    {
        RecipeProject project = Parse(RecipeFixture.DefaultRecipesJson);

        Assert.Equal((RecipeFixture.Version, true, (RecipeLauncher?)null), (project.Version, project.Enabled, project.Launcher));
        Assert.Equal(RecipeFixture.PackIds, project.Packs.Select(pack => pack.Id));
        RecipePack badges = project.Packs[0];
        Assert.Equal((RecipePackType.Badge, true, "Linia 1\nLinia 2"), (badges.Type, badges.Default, badges.Description));
        Assert.Equal(Path.Combine(BaseDirectory, "mappings", "badges.txt"), badges.MappingPath);
        Assert.Equal(((int?)null, (IconCorner?)null), (badges.Size, badges.Corner));
        RecipePack marks = project.Packs[2];
        Assert.Equal((RecipePackType.Overlay, false, (IconCorner?)IconCorner.TopRight, (string?)null), (marks.Type, marks.Default, marks.Corner, marks.Description));
        Assert.Equal(new Announcement(new DateOnly(2026, 10, 20), "Start", "Pierwsza linia.\nDruga linia."), project.Announcements.Single());
        OptionPreset preset = project.OptionPresets.Single();
        Assert.Equal(("mass-pvp", "Masowe PvP"), (preset.Id, preset.Name));
        Assert.Equal(new[] { new IniValue("Video", "EffectDetail", "0") }, preset.Values);
    }

    [Fact]
    public void Parse_ReadsOptionalFields()
    {
        RecipeProject project = Parse("""
            {
              "format": 1, "version": "1", "enabled": false,
              "launcher": { "version": "1.0.0", "file": "../bin/CpLauncher.exe" },
              "packs": [ { "id": "tags", "name": "Tagi", "type": "tag", "mapping": "t.txt", "size": 12, "corner": "br", "colors": { "cc": "#112233" } } ]
            }
            """);

        Assert.False(project.Enabled);
        Assert.Equal(new RecipeLauncher("1.0.0", Path.GetFullPath(Path.Combine(BaseDirectory, "..", "bin", "CpLauncher.exe"))), project.Launcher);
        RecipePack tags = project.Packs.Single();
        Assert.Equal((12, IconCorner.BottomRight), (tags.Size, tags.Corner));
        Assert.Equal(new Rgb(0x11, 0x22, 0x33), tags.Colors!["CC"]);
        Assert.Empty(project.Announcements);
        Assert.Empty(project.OptionPresets);
    }

    [Theory]
    [InlineData("""{ "format": 2, "version": "1", "packs": [] }""", "recipes.json: format: must be 1, not 2.")]
    [InlineData("""{ "format": 1, "packs": [] }""", "version: is missing")]
    [InlineData("""{ "format": 1, "version": "1", "packs": [], "defualt": true }""", "the root: unknown field 'defualt'")]
    [InlineData("""{ "format": 1, "version": "1", "packs": [] }""", "packs: lists no packs")]
    [InlineData("""{ "format": 1, "version": "1", "packs": {} }""", "packs: must be a JSON array")]
    [InlineData("""{ "format": 1, "version": "1", "launcher": { "version": "1.0.0" }, "packs": [] }""", "launcher.file: is missing")]
    [InlineData("""{ "format": 1, "version": "1", "packs": [ { "id": "a", "name": "A", "type": "tag", "mapping": "t.txt", "description": "tab\there" } ] }""", "packs[0].description: must have at most")]
    public void Parse_RejectsBadRoots(string json, string expected)
    {
        FormatException exception = Assert.Throws<FormatException>(() => Parse(json));

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("\"type\": \"sticker\"", "is not badge, tag or overlay")]
    [InlineData("\"type\": \"overlay\", \"size\": 8", "an overlay has the size of its PNG")]
    [InlineData("\"type\": \"badge\", \"colors\": { \"CC\": \"#E02020\" }", "only tag packs have category colours")]
    [InlineData("\"type\": \"tag\", \"colors\": { \"CC\": \"E02020\" }", "must be a #RRGGBB string")]
    [InlineData("\"type\": \"tag\", \"corner\": \"middle\"", "must be br, bl, tr or tl")]
    [InlineData("\"type\": \"tag\", \"size\": \"8\"", "must be an integer")]
    [InlineData("\"type\": \"tag\", \"default\": \"yes\"", "must be true or false")]
    [InlineData("\"type\": \"tag\", \"mapping\": \"\"", "must be a non-empty string")]
    public void Parse_RejectsBadPacks(string fields, string expected)
    {
        string mapping = fields.Contains("\"mapping\"") ? string.Empty : ", \"mapping\": \"t.txt\"";
        string json = $$"""{ "format": 1, "version": "1", "packs": [ { "id": "a", "name": "A", {{fields}}{{mapping}} } ] }""";

        FormatException exception = Assert.Throws<FormatException>(() => Parse(json));

        Assert.Contains("packs[0]", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("""{ "date": "20.10.2026", "title": "T", "text": "x" }""", "must be yyyy-MM-dd")]
    [InlineData("""{ "date": "2026-10-20", "title": "T", "text": 5 }""", "must be a string or an array of lines")]
    [InlineData("""{ "date": "2026-10-20", "title": "T", "text": "x", "html": "<b>" }""", "unknown field 'html'")]
    public void Parse_RejectsBadAnnouncements(string announcement, string expected)
    {
        string json = $$"""{ "format": 1, "version": "1", "packs": [ { "id": "a", "name": "A", "type": "tag", "mapping": "t.txt" } ], "announcements": [ {{announcement}} ] }""";

        FormatException exception = Assert.Throws<FormatException>(() => Parse(json));

        Assert.Contains("announcements[0]", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Parse_RejectsPresetValuesThatAreNotStringsOrNumbers()
    {
        string json = """{ "format": 1, "version": "1", "packs": [ { "id": "a", "name": "A", "type": "tag", "mapping": "t.txt" } ], "optionPresets": [ { "id": "p", "name": "P", "values": { "Video": { "Bloom": true } } } ] }""";

        FormatException exception = Assert.Throws<FormatException>(() => Parse(json));

        Assert.Contains("optionPresets[0].values.Video.Bloom", exception.Message);
    }

    [Fact]
    public void Parse_RejectsInvalidJsonAndDuplicateFields()
    {
        Assert.Throws<FormatException>(() => Parse("{ \"format\": 1, "));
        Assert.Throws<FormatException>(() => Parse("""{ "format": 1, "format": 1, "version": "1", "packs": [] }"""));
    }

    [Fact]
    public void Load_AcceptsTheFolderOrTheFile()
    {
        using var fixture = new RecipeFixture();

        RecipeProject fromFolder = RecipeProject.Load(fixture.RecipesPath);
        RecipeProject fromFile = RecipeProject.Load(Path.Combine(fixture.RecipesPath, RecipeProject.FileName));

        Assert.Equal(Path.GetFullPath(fixture.RecipesPath), fromFolder.BaseDirectory);
        Assert.Equal(fromFolder.BaseDirectory, fromFile.BaseDirectory);
        Assert.Throws<FileNotFoundException>(() => RecipeProject.Load(fixture.Temp.Combine("missing")));
    }

    [Fact]
    public void Compile_TurnsMappingsIntoOperationsWithTheApprovedStyles()
    {
        using var fixture = new RecipeFixture();

        CompiledRecipes compiled = RecipeCompiler.Compile(RecipeProject.Load(fixture.RecipesPath));

        BadgeOperation badge = Assert.IsType<BadgeOperation>(compiled.Packs[0].Entries.Single().Operation);
        Assert.Equal(("skill0365", "skill1303", 20, IconCorner.BottomRight, 1), (badge.Target, badge.Badge, badge.Style.Size, badge.Style.Corner, badge.Style.Border));
        Assert.Equal("skill0365 <- skill1303  # Dance -> Might", compiled.Packs[0].Entries.Single().Summary);
        Assert.Equal(new TagOperation("Icon.utx", "skill0102", Rgb.ParseHex("#E02020"), 8, IconCorner.TopLeft), compiled.Packs[1].Entries.Single().Operation);
        OverlayOperation overlay = Assert.IsType<OverlayOperation>(compiled.Packs[2].Entries.Single().Operation);
        Assert.Equal(IconCorner.TopRight, overlay.Corner);
        byte[] asset = Assert.Single(compiled.Assets).Value;
        Assert.Equal($"assets/{ContentHash.Sha256Hex(asset)}.png", overlay.Image);
        Assert.Equal(ContentHash.Sha256Hex(asset), overlay.ImageSha256);
        Assert.Equal(RecipeFixture.OverlayImage().Pixels, PngCodec.Decode(asset).Pixels);
    }

    [Fact]
    public void Compile_ReportsThePackOfABadMappingLine()
    {
        using var fixture = new RecipeFixture();
        fixture.Temp.WriteText(Path.Combine("recipes", "mappings", "tags.txt"), "skill0102  SLOW  # unknown category\n");

        FormatException exception = Assert.Throws<FormatException>(() => RecipeCompiler.Compile(RecipeProject.Load(fixture.RecipesPath)));

        Assert.Contains("Pack 'tags'", exception.Message);
        Assert.Contains("unknown category 'SLOW'", exception.Message);
    }

    [Fact]
    public void Compile_RejectsMissingMappingsAndOversizedOverlays()
    {
        using var fixture = new RecipeFixture();
        fixture.Temp.WriteBytes(Path.Combine("recipes", "mappings", "marks", "dot.png"), PngCodec.Encode(new RgbaImage(40, 40)));

        Assert.Throws<InvalidDataException>(() => RecipeCompiler.Compile(RecipeProject.Load(fixture.RecipesPath)));

        File.Delete(Path.Combine(fixture.RecipesPath, "mappings", "tags.txt"));
        Assert.Throws<FileNotFoundException>(() => RecipeCompiler.Compile(RecipeProject.Load(fixture.RecipesPath)));
    }

    [Fact]
    public void CommittedRecipes_UseTheApprovedMappingsAndFitTheManifest()
    {
        string root = FindRepositoryRoot();

        RecipeProject project = RecipeProject.Load(Path.Combine(root, "recipes"));
        CompiledRecipes compiled = RecipeCompiler.Compile(project);

        Assert.Equal(new[] { "dance-song-badges", "chant-badges", "debuff-tags", "mob-debuff-tags" }, project.Packs.Select(pack => pack.Id));
        string mappings = Path.Combine(root, "mappings");
        Assert.Equal(
            new[]
            {
                Path.Combine(mappings, "dances-songs.txt"),
                Path.Combine(mappings, "chants.txt"),
                Path.Combine(mappings, "debuff-tags.txt"),
                Path.Combine(mappings, "mob-debuff-tags.txt"),
            },
            project.Packs.Select(pack => pack.MappingPath));
        Assert.Equal(new[] { 22, 22, 90, 25 }, compiled.Packs.Select(pack => pack.Entries.Count));
        Assert.All(compiled.Packs[0].Operations.Concat(compiled.Packs[1].Operations), operation => Assert.Equal(20, Assert.IsType<BadgeOperation>(operation).Style.Size));
        var manifest = new Manifest(
            1,
            project.Version,
            project.Enabled,
            null,
            new Dictionary<string, IReadOnlyList<string>> { ["Icon.utx"] = new[] { ManifestParserTests.OriginalHash } },
            project.Announcements,
            compiled.Packs.Select(pack => pack.ToManifestPack()).ToList(),
            project.OptionPresets);
        ManifestWriter.WriteChecked(manifest);
    }

    private static RecipeProject Parse(string json)
    {
        return RecipeProject.Parse(Encoding.UTF8.GetBytes(json), BaseDirectory);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "L2Mod.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
