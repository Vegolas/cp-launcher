using L2Mod.Core.Effects;
using L2Mod.Core.Manifests;
using L2Mod.Core.Publishing;

namespace L2Mod.Core.Tests.Publishing;

/// <summary>The leader's build with effect packs: their originals, options in the manifest, the preview sheets.</summary>
public sealed class EffectDistBuilderTests : IDisposable
{
    private const string Recipes = """
        {
          "format": 1,
          "version": "2026.11.09.1",
          "packs": [
            { "id": "target-icons", "name": "Ikony nad celem", "type": "target-icon", "mapping": "mappings/icons.txt" },
            { "id": "skill-effect-swaps", "name": "Zamiana efektów", "default": true, "type": "effect-swap", "mapping": "mappings/swaps.txt" },
            { "id": "gold-click-arrow", "name": "Złota strzałka", "type": "gold-click-arrow" },
          ],
        }
        """;

    private readonly TempDirectory _temp = new TempDirectory();
    private readonly string _icons;
    private readonly string _skillEffects;
    private readonly string _effects;

    public EffectDistBuilderTests()
    {
        _icons = _temp.WriteBytes(Path.Combine("originals", ClientFiles.IconUtx), TestEffectPackages.BuildIconUtx());
        _skillEffects = _temp.WriteBytes(Path.Combine("originals", ClientFiles.LineageSkillEffect), TestEffectPackages.BuildSkillEffects());
        _effects = _temp.WriteBytes(Path.Combine("originals", ClientFiles.LineageEffect), TestEffectPackages.BuildEffects());
        _temp.WriteText(Path.Combine("recipes", "mappings", "icons.txt"), "1071  # Surrender to Water\n1323 @1048  # Noblesse Blessing | Też po Bless the Soul.\n");
        _temp.WriteText(Path.Combine("recipes", "mappings", "swaps.txt"), "1235 1175  # Hydro Blast ↔ Aqua Swirl\n");
        _temp.WriteText(Path.Combine("recipes", "recipes.json"), Recipes);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Build_SupportsEveryOriginalThePacksChange_WithOneOptionPerLine()
    {
        DistBuildResult build = DistBuilder.Build(RecipeProject.Load(_temp.Combine("recipes")), new[] { _icons, _skillEffects, _effects });

        Assert.Equal(
            new[] { ClientFiles.IconUtx, ClientFiles.LineageEffect, ClientFiles.LineageSkillEffect },
            build.Manifest.SupportedOriginals.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(ContentHash.Sha256Hex(File.ReadAllBytes(_skillEffects)), build.Originals[ClientFiles.LineageSkillEffect]);
        Pack icons = build.Manifest.Packs[0];
        Assert.Equal(new[] { "1071", "1323" }, icons.Options.Select(option => option.Id));
        Assert.Equal(("Noblesse Blessing", "Też po Bless the Soul."), (icons.Options[1].Name, icons.Options[1].Description));
        Assert.All(icons.Options, option => Assert.False(option.Default));
        Assert.True(Assert.Single(build.Manifest.Packs[1].Options).Default);
        Assert.IsType<GoldClickArrowOperation>(Assert.Single(build.Manifest.Packs[2].Operations));
        Assert.Equal(new[] { "target-icons", "skill-effect-swaps" }, build.Previews.Select(preview => preview.PackId));
        Assert.Equal(PackPreview.IconOverTarget, build.Previews[0].Caption);
        Assert.Contains(build.Notes, note => note.StartsWith("LineageSkillEffect.u: checked:", StringComparison.Ordinal));
        Assert.Contains("LineageEffect.u: click-to-move arrow recoloured gold", build.Notes);
    }

    [Fact]
    public void Build_RefusesEffectPacksWithoutTheirOriginal()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => DistBuilder.Build(RecipeProject.Load(_temp.Combine("recipes")), new[] { _icons, _skillEffects }));

        Assert.Contains(ClientFiles.LineageEffect, exception.Message);
    }

    [Fact]
    public void Build_RefusesAnIconForASkillWithoutAClass()
    {
        _temp.WriteText(Path.Combine("recipes", "mappings", "icons.txt"), "1323  # Noblesse Blessing\n");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => DistBuilder.Build(RecipeProject.Load(_temp.Combine("recipes")), new[] { _icons, _skillEffects, _effects }));

        Assert.Contains("l2_1323_skill", exception.Message);
    }

    [Theory]
    [InlineData("\"type\": \"gold-click-arrow\", \"mapping\": \"m.txt\"", "has no mapping")]
    [InlineData("\"type\": \"target-icon\", \"mapping\": \"m.txt\", \"size\": 8", "only icon packs")]
    [InlineData("\"type\": \"effect-swap\", \"mapping\": \"m.txt\", \"corner\": \"br\"", "only icon packs")]
    public void Recipes_RejectIconFieldsOnEffectPacks(string fields, string expected)
    {
        string json = $$"""{ "format": 1, "version": "1", "packs": [ { "id": "p", "name": "P", {{fields}} } ] }""";

        FormatException exception = Assert.Throws<FormatException>(() => RecipeProject.Parse(System.Text.Encoding.UTF8.GetBytes(json), _temp.Root));

        Assert.Contains(expected, exception.Message);
    }
}
