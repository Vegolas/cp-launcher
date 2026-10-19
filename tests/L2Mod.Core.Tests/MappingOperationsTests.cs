using L2Mod.Core.Imaging;
using L2Mod.Core.Operations;
using L2Mod.Core.Recipes;

namespace L2Mod.Core.Tests;

public class MappingOperationsTests
{
    [Fact]
    public void RepositoryDancesSongs_LoadAsTwentyPixelBadges()
    {
        IReadOnlyList<BadgeOperation> operations = MappingOperations.LoadBadges(MappingPath("dances-songs.txt"));

        Assert.Equal(22, operations.Count);
        Assert.All(operations, operation => AssertApprovedBadge(operation));
        Assert.Contains(operations, operation => operation.Target == "skill0365" && operation.Badge == "skill1303");
    }

    [Fact]
    public void RepositoryChants_LoadAsTwentyPixelBadges()
    {
        IReadOnlyList<BadgeOperation> operations = MappingOperations.LoadBadges(MappingPath("chants.txt"));

        Assert.Equal(22, operations.Count);
        Assert.All(operations, operation => AssertApprovedBadge(operation));
        Assert.Contains(operations, operation => operation.Target == "skill1007" && operation.Badge == "skill1068");
        Assert.Contains(operations, operation => operation.Target == "skill1282" && operation.Badge == "skill1204");
    }

    [Fact]
    public void RepositoryDebuffTags_LoadWithCategoryColours()
    {
        IReadOnlyList<TagOperation> operations = MappingOperations.LoadDebuffTags(MappingPath("debuff-tags.txt"));

        Assert.Equal(90, operations.Count);
        Assert.All(operations, operation =>
        {
            Assert.Equal("Icon.utx", operation.File);
            Assert.Equal(8, operation.Size);
            Assert.Equal(IconCorner.TopLeft, operation.Corner);
        });
        Assert.Equal(31, operations.Count(operation => operation.Color == Rgb.ParseHex("#E02020")));
        Assert.Equal(20, operations.Count(operation => operation.Color == Rgb.ParseHex("#A040FF")));
        Assert.Equal(39, operations.Count(operation => operation.Color == Rgb.ParseHex("#FF9900")));
        Assert.Contains(operations, operation => operation.Target == "skill0002" && operation.Color == Rgb.ParseHex("#E02020"));
    }

    [Fact]
    public void RepositoryMappings_DoNotShareTargets()
    {
        var targets = new List<string>();
        targets.AddRange(MappingOperations.LoadBadges(MappingPath("dances-songs.txt")).Select(operation => operation.Target));
        targets.AddRange(MappingOperations.LoadBadges(MappingPath("chants.txt")).Select(operation => operation.Target));
        targets.AddRange(MappingOperations.LoadDebuffTags(MappingPath("debuff-tags.txt")).Select(operation => operation.Target));

        Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TagMapping_ReadsEntriesAndLabels()
    {
        string[] lines = { "# header", "skill0002  CC    # Confusion", "skill1164 stat" };

        IReadOnlyList<TagMappingEntry> entries = TagMapping.Parse(lines);

        Assert.Equal(new TagMappingEntry("skill0002", "CC", "Confusion", 2), entries[0]);
        Assert.Equal(new TagMappingEntry("skill1164", "stat", string.Empty, 3), entries[1]);
    }

    [Fact]
    public void FromTags_RejectsUnknownCategory()
    {
        IReadOnlyList<TagMappingEntry> entries = TagMapping.Parse(new[] { "skill0002 CC", "skill0048 HEAL" });

        FormatException exception = Assert.Throws<FormatException>(() => MappingOperations.FromTags(entries, "Icon.utx", RecipeDefaults.DebuffColors, 8, IconCorner.TopLeft));

        Assert.Contains("Line 2", exception.Message);
        Assert.Contains("HEAL", exception.Message);
    }

    [Theory]
    [InlineData("skill0002")]
    [InlineData("skill0002 CC extra")]
    public void TagMapping_RejectsWrongColumnCount(string line)
    {
        FormatException exception = Assert.Throws<FormatException>(() => TagMapping.Parse(new[] { line }));

        Assert.Contains("<target> <category>", exception.Message);
    }

    private static void AssertApprovedBadge(BadgeOperation operation)
    {
        Assert.Equal("Icon.utx", operation.File);
        Assert.Equal(20, operation.Style.Size);
        Assert.Equal(IconCorner.BottomRight, operation.Style.Corner);
        Assert.Equal(1, operation.Style.Border);
        Assert.Equal(Rgb.Black, operation.Style.BorderColor);
    }

    private static string MappingPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "L2Mod.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("L2Mod.slnx not found above the test output folder.");
        }

        return Path.Combine(directory.FullName, "mappings", fileName);
    }
}
