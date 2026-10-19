namespace L2Mod.Core.Recipes;

public sealed record MappingLine(string Key, string Value, string Label, int LineNumber);

/// <summary>
/// Line format shared by the mapping files: "key value  # label" per line; blank lines and lines starting
/// with # are ignored, keys must be unique (case-insensitive) and at least one entry is required.
/// </summary>
public static class MappingFile
{
    /// <param name="expected">Column description used in error messages, e.g. "&lt;target&gt; &lt;badge&gt;".</param>
    public static IReadOnlyList<MappingLine> Parse(IReadOnlyList<string> lines, string expected)
    {
        var entries = new List<MappingLine>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i];
            string label = string.Empty;
            int commentStart = line.IndexOf('#');
            if (commentStart >= 0)
            {
                label = line.Substring(commentStart + 1).Trim();
                line = line.Substring(0, commentStart);
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (parts.Length != 2)
            {
                throw new FormatException($"Line {lineNumber}: expected '{expected}', got '{line.Trim()}'.");
            }

            if (!keys.Add(parts[0]))
            {
                throw new FormatException($"Line {lineNumber}: {parts[0]} is mapped twice.");
            }

            entries.Add(new MappingLine(parts[0], parts[1], label, lineNumber));
        }

        if (entries.Count == 0)
        {
            throw new FormatException("Mapping file has no entries.");
        }

        return entries;
    }
}
