using L2Mod.Core.Badges;

namespace L2Mod.Core.Tests;

public class BadgeMappingTests
{
    [Fact]
    public void Parse_ReadsEntriesLabelsAndSkipsComments()
    {
        string[] lines =
        {
            "# header",
            "",
            "skill0365  skill1303  # Dance of Siren -> Wild Magic",
            "   skill0276\tskill1085",
        };

        IReadOnlyList<BadgeMappingEntry> entries = BadgeMapping.Parse(lines);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new BadgeMappingEntry("skill0365", "skill1303", "Dance of Siren -> Wild Magic", 3), entries[0]);
        Assert.Equal(new BadgeMappingEntry("skill0276", "skill1085", string.Empty, 4), entries[1]);
    }

    [Theory]
    [InlineData("skill0365")]
    [InlineData("skill0365 skill1303 extra")]
    public void Parse_RejectsWrongColumnCount(string line)
    {
        FormatException exception = Assert.Throws<FormatException>(() => BadgeMapping.Parse(new[] { line }));

        Assert.Contains("Line 1", exception.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateTargets()
    {
        string[] lines = { "skill0365 skill1303", "SKILL0365 skill1059" };

        Assert.Throws<FormatException>(() => BadgeMapping.Parse(lines));
    }

    [Fact]
    public void Parse_RejectsEmptyMapping()
    {
        Assert.Throws<FormatException>(() => BadgeMapping.Parse(new[] { "# nothing" }));
    }

    [Fact]
    public void RepositoryMapping_IsValid()
    {
        string path = Path.Combine(FindModdingRoot(), "mappings", "dances-songs.txt");

        IReadOnlyList<BadgeMappingEntry> entries = BadgeMapping.Load(path);

        Assert.Equal(22, entries.Count);
        Assert.Contains(entries, entry => entry.Target == "skill0365" && entry.Badge == "skill1303");
        Assert.Contains(entries, entry => entry.Target == "skill0276" && entry.Badge == "skill1085");
        Assert.Contains(entries, entry => entry.Target == "skill0273" && entry.Badge == "skill1059");
        Assert.Contains(entries, entry => entry.Target == "skill0304" && entry.Badge == "skill1045");
    }

    private static string FindModdingRoot()
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

        return directory.FullName;
    }
}
