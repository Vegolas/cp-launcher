using System.Text;
using System.Text.Json.Nodes;
using L2Mod.Core.Effects;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Publishing;
using L2Mod.Core.Recipes;

namespace L2Mod.Core.Tests;

/// <summary>Packs with options and the effect operations in the manifest, the mapping files and the recipe keys.</summary>
public class EffectManifestTests
{
    private const string EffectsJson = """
        [
          { "id": "target-icons", "name": "Ikony nad celem", "default": false,
            "options": [
              { "id": "1071", "name": "Surrender to Water",
                "operations": [ { "op": "target-icon", "file": "LineageSkillEffect.u", "skill": 1071, "effectClass": 1071, "seconds": 5 } ] },
              { "id": "1323", "name": "Noblesse Blessing", "description": "Też po Bless the Soul.", "default": true,
                "operations": [ { "op": "target-icon", "file": "lineageskilleffect.u", "skill": 1323, "effectClass": 1048 } ] }
            ] },
          { "id": "skill-effect-swaps", "name": "Zamiana efektów", "default": true,
            "options": [
              { "id": "1235-1175", "name": "Hydro Blast ↔ Aqua Swirl",
                "operations": [ { "op": "effect-swap", "file": "LineageSkillEffect.u", "skills": [1235, 1175] } ] }
            ] },
          { "id": "gold-click-arrow", "name": "Złota strzałka", "default": false,
            "operations": [ { "op": "gold-click-arrow", "file": "LineageEffect.u" } ] }
        ]
        """;

    [Fact]
    public void Parse_ReadsPacksWithOptionsAndEffectOperations()
    {
        ManifestReadResult result = Parse(Example());

        Assert.Empty(result.Messages);
        Pack icons = result.Manifest.Packs[3];
        Assert.True(icons.HasOptions);
        Assert.Equal(new[] { "1071", "1323" }, icons.Options.Select(option => option.Id));
        Assert.Equal(new[] { false, true }, icons.Options.Select(option => option.Default));
        Assert.Equal("Też po Bless the Soul.", icons.Options[1].Description);
        Assert.Equal(new TargetIconOperation(1323, 1048, EffectKinds.DefaultIconSeconds), icons.Options[1].Operations.Single());
        Assert.Equal(ClientFiles.LineageSkillEffect, icons.Options[1].Operations.Single().File);
        Assert.Equal(icons.Options.SelectMany(option => option.Operations), icons.Operations);
        Pack swaps = result.Manifest.Packs[4];
        Assert.True(swaps.Options.Single().Default);
        Assert.Equal(new EffectSwapOperation(1235, 1175), swaps.Options.Single().Operations.Single());
        Pack arrow = result.Manifest.Packs[5];
        Assert.False(arrow.HasOptions);
        Assert.IsType<GoldClickArrowOperation>(arrow.Operations.Single());
    }

    [Fact]
    public void GetOperations_TakesOptionsByTheirSelectionId()
    {
        Manifest manifest = Parse(Example()).Manifest;
        Pack icons = manifest.Packs[3];

        IReadOnlyList<IPatchOperation> operations = manifest.GetOperations(
            ClientFiles.LineageSkillEffect,
            new[] { "target-icons", icons.GetSelectionId(icons.Options[1]), "skill-effect-swaps/1235-1175", "gold-click-arrow" });

        Assert.Equal(new IPatchOperation[] { new TargetIconOperation(1323, 1048, 5), new EffectSwapOperation(1235, 1175) }, operations);
        Assert.Equal("target-icons/1323", icons.GetSelectionId(icons.Options[1]));
        Assert.Single(manifest.GetOperations(ClientFiles.LineageEffect, new[] { "gold-click-arrow" }));
        Assert.Empty(manifest.GetOperations(ClientFiles.IconUtx, new[] { "target-icons/1071", "gold-click-arrow" }));
        Assert.Contains("target-icons/1071", manifest.GetAllSelectionIds());
        Assert.DoesNotContain("target-icons", manifest.GetAllSelectionIds());
    }

    [Theory]
    [InlineData("""{ "op": "target-icon", "file": "Icon.utx", "skill": 1071 }""", "changes LineageSkillEffect.u, not Icon.utx")]
    [InlineData("""{ "op": "gold-click-arrow", "file": "LineageSkillEffect.u" }""", "changes LineageEffect.u")]
    [InlineData("""{ "op": "badge", "file": "LineageEffect.u", "target": "a", "badge": "b" }""", "changes icons in Icon.utx")]
    [InlineData("""{ "op": "target-icon", "file": "LineageSkillEffect.u", "skill": 0 }""", "skill ids from 1 to 99999")]
    [InlineData("""{ "op": "target-icon", "file": "LineageSkillEffect.u", "skill": 1071, "seconds": 61 }""", "seconds 61")]
    [InlineData("""{ "op": "target-icon", "file": "LineageSkillEffect.u", "skill": 1071, "size": 2 }""", "unknown field 'size'")]
    [InlineData("""{ "op": "effect-swap", "file": "LineageSkillEffect.u", "skills": [1235] }""", "two skill ids")]
    [InlineData("""{ "op": "effect-swap", "file": "LineageSkillEffect.u", "skills": [1235, 1235] }""", "two different skills")]
    [InlineData("""{ "op": "effect-swap", "file": "system_season/LineageSkillEffect.u", "skills": [1, 2] }""", "is not allowed")]
    public void Parse_SkipsAPackWithABadEffectOperation(string operation, string expected)
    {
        JsonObject root = Example();
        Pack(root, 5)["operations"] = new JsonArray(JsonNode.Parse(operation));

        ManifestReadResult result = Parse(root);

        Assert.DoesNotContain(result.Manifest.Packs, pack => pack.Id == "gold-click-arrow");
        Assert.Contains(expected, Assert.Single(result.Messages));
    }

    [Theory]
    [InlineData("both", "both operations and options", ManifestSkipReason.InvalidValue)]
    [InlineData("empty", "no options", ManifestSkipReason.InvalidValue)]
    [InlineData("duplicate", "another option has the same id", ManifestSkipReason.DuplicateId)]
    [InlineData("badId", "lower-case letters", ManifestSkipReason.InvalidValue)]
    [InlineData("noOperations", "'operations' is missing", ManifestSkipReason.InvalidValue)]
    [InlineData("unknownOp", "unknown op", ManifestSkipReason.UnknownOperation)]
    public void Parse_SkipsAPackWithBadOptions(string problem, string expected, ManifestSkipReason reason)
    {
        JsonObject root = Example();
        JsonObject pack = Pack(root, 3);
        JsonArray options = pack["options"]!.AsArray();
        switch (problem)
        {
            case "both":
                pack["operations"] = new JsonArray();
                break;
            case "empty":
                options.Clear();
                break;
            case "duplicate":
                options[1]!["id"] = "1071";
                break;
            case "badId":
                options[0]!["id"] = "Water";
                break;
            case "noOperations":
                options[0]!.AsObject().Remove("operations");
                break;
            default:
                options[0]!["operations"]![0]!["op"] = "hover";
                break;
        }

        ManifestReadResult result = Parse(root);

        Assert.DoesNotContain(result.Manifest.Packs, item => item.Id == "target-icons");
        ManifestNote note = Assert.Single(result.Notes);
        Assert.Contains(expected, note.Message);
        Assert.Equal(reason, note.Reason);
        Assert.Equal("target-icons", note.ItemId);
    }

    [Fact]
    public void Parse_CountsTheOperationsOfAllOptionsAgainstThePackLimit()
    {
        ManifestReadResult result = Parse(Example(), new ManifestLimits { MaxOperationsPerPack = 1 });

        Assert.DoesNotContain(result.Manifest.Packs, pack => pack.Id == "target-icons");
        Assert.Contains(result.Notes, note => note.ItemId == "target-icons" && note.Reason == ManifestSkipReason.OverLimit);
    }

    [Fact]
    public void Writer_WritesOptionsAndEffectOperationsThatReadBackTheSame()
    {
        Manifest manifest = Parse(Example()).Manifest;

        byte[] json = ManifestWriter.WriteChecked(manifest);
        Manifest reread = ManifestParser.Parse(json).Manifest;

        Assert.Equal(manifest.Packs.Select(pack => pack.Options.Count), reread.Packs.Select(pack => pack.Options.Count));
        string text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"effectClass\": 1048", text);
        Assert.Contains("\"seconds\": 5", text);
        Assert.Contains("Hydro Blast ↔ Aqua Swirl", text);
        Assert.Equal(json, ManifestWriter.Write(reread));
    }

    [Fact]
    public void RecipeKey_OfIconUtxIsUnchanged_AndEffectFilesHaveTheirOwnEngine()
    {
        var operations = new IPatchOperation[] { new TargetIconOperation(1071, 1071, 5) };
        string expectedIcon = ContentHash.Sha256Hex(Encoding.UTF8.GetBytes("engine 1\n"));

        Assert.Equal(expectedIcon, PatchInstaller.ComputeRecipeKey(ClientFiles.IconUtx, Array.Empty<IPatchOperation>()));
        Assert.NotEqual(
            PatchInstaller.ComputeRecipeKey(ClientFiles.LineageSkillEffect, operations),
            PatchInstaller.ComputeRecipeKey(ClientFiles.LineageEffect, operations));
        Assert.NotEqual(
            PatchInstaller.ComputeRecipeKey(ClientFiles.LineageSkillEffect, operations),
            PatchInstaller.ComputeRecipeKey(ClientFiles.LineageSkillEffect, new IPatchOperation[] { new TargetIconOperation(1071, 1071, 6) }));
    }

    [Fact]
    public void Mappings_ReadSwapsAndIconsWithNamesAndDescriptions()
    {
        IReadOnlyList<EffectSwapEntry> swaps = EffectMappings.ParseSwaps(new[] { "# pairs", "1235 1175  # Hydro Blast ↔ Aqua Swirl | Zamiana." });
        IReadOnlyList<TargetIconEntry> icons = EffectMappings.ParseTargetIcons(new[]
        {
            "1071  # Surrender to Water",
            "",
            "1323 @1048 7  # Noblesse Blessing | Też po Bless the Soul.",
        });

        EffectSwapEntry swap = Assert.Single(swaps);
        Assert.Equal(("1235-1175", "Hydro Blast ↔ Aqua Swirl", "Zamiana."), (swap.Id, swap.Label.Name, swap.Label.Description));
        Assert.Equal(new TargetIconOperation(1071, 1071, 5), icons[0].Operation);
        Assert.Null(icons[0].Label.Description);
        Assert.Equal(new TargetIconOperation(1323, 1048, 7), icons[1].Operation);
        Assert.Equal(("1323", "Noblesse Blessing"), (icons[1].Id, icons[1].Label.Name));
    }

    [Theory]
    [InlineData("1071", "must name the choice")]
    [InlineData("1071 @1071 5 9  # x", "expected")]
    [InlineData("1071 0  # x", "whole seconds")]
    [InlineData("1071 @abc  # x", "is not a skill id")]
    [InlineData("1071  # a\n1072 @1071  # b", "listed twice")]
    [InlineData("100000  # x", "is not a skill id")]
    public void Mappings_RejectBadIconLines(string text, string expected)
    {
        FormatException exception = Assert.Throws<FormatException>(() => EffectMappings.ParseTargetIcons(text.Split('\n')));

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("1235 1175", "must name the choice")]
    [InlineData("1235 1175  # a\n1175 1230  # b", "one swap only")]
    [InlineData("1235 1235  # a", "one swap only")]
    [InlineData("1235  # a", "expected")]
    public void Mappings_RejectBadSwapLines(string text, string expected)
    {
        FormatException exception = Assert.Throws<FormatException>(() => EffectMappings.ParseSwaps(text.Split('\n')));

        Assert.Contains(expected, exception.Message);
    }

    private static JsonObject Example()
    {
        JsonObject root = JsonNode.Parse(ManifestParserTests.ExampleJson)!.AsObject();
        JsonArray packs = root["packs"]!.AsArray();
        foreach (JsonNode? pack in JsonNode.Parse(EffectsJson)!.AsArray().ToList())
        {
            packs.Add(pack!.DeepClone());
        }

        root["supportedOriginals"]![ClientFiles.LineageSkillEffect] = new JsonArray(ManifestParserTests.OriginalHash);
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
}
