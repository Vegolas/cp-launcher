using L2Mod.Core.Recipes;

namespace L2Mod.Core.Badges;

public sealed record BadgeMappingEntry(string Target, string Badge, string Label, int LineNumber);

/// <summary>
/// Mapping file: one "target badge  # label" pair per line, both texture names; blank lines and
/// lines starting with # are ignored.
/// </summary>
public static class BadgeMapping
{
    public static IReadOnlyList<BadgeMappingEntry> Load(string path)
    {
        return Parse(File.ReadAllLines(path));
    }

    public static IReadOnlyList<BadgeMappingEntry> Parse(IReadOnlyList<string> lines)
    {
        var entries = new List<BadgeMappingEntry>();
        foreach (MappingLine line in MappingFile.Parse(lines, "<target> <badge>"))
        {
            entries.Add(new BadgeMappingEntry(line.Key, line.Value, line.Label, line.LineNumber));
        }

        return entries;
    }
}
