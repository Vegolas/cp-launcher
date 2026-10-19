namespace L2Mod.Core.Recipes;

public sealed record TagMappingEntry(string Target, string Category, string Label, int LineNumber);

/// <summary>Mapping file of category tags: one "target category  # label" pair per line (e.g. debuff-tags.txt).</summary>
public static class TagMapping
{
    public static IReadOnlyList<TagMappingEntry> Load(string path)
    {
        return Parse(File.ReadAllLines(path));
    }

    public static IReadOnlyList<TagMappingEntry> Parse(IReadOnlyList<string> lines)
    {
        var entries = new List<TagMappingEntry>();
        foreach (MappingLine line in MappingFile.Parse(lines, "<target> <category>"))
        {
            entries.Add(new TagMappingEntry(line.Key, line.Value, line.Label, line.LineNumber));
        }

        return entries;
    }
}
