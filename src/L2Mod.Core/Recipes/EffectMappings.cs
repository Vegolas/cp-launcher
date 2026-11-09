using System.Globalization;
using L2Mod.Core.Effects;

namespace L2Mod.Core.Recipes;

/// <summary>A choice a member can tick: the label of a mapping line, "name" or "name | description".</summary>
public sealed record OptionLabel(string Name, string? Description)
{
    public const char Separator = '|';

    public static OptionLabel Parse(string label, int lineNumber)
    {
        string name = label;
        string? description = null;
        int separator = label.IndexOf(Separator);
        if (separator >= 0)
        {
            name = label.Substring(0, separator).Trim();
            description = label.Substring(separator + 1).Trim();
            if (description.Length == 0)
            {
                description = null;
            }
        }

        if (name.Length == 0)
        {
            throw new FormatException($"Line {lineNumber}: the comment must name the choice members see (\"# name\" or \"# name | description\").");
        }

        return new OptionLabel(name, description);
    }
}

public sealed record EffectSwapEntry(EffectSwapOperation Operation, OptionLabel Label, int LineNumber)
{
    /// <summary>The option id in the manifest: "&lt;first&gt;-&lt;second&gt;".</summary>
    public string Id
    {
        get
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Operation.First}-{Operation.Second}");
        }
    }
}

public sealed record TargetIconEntry(TargetIconOperation Operation, OptionLabel Label, int LineNumber)
{
    /// <summary>The option id in the manifest: the skill id.</summary>
    public string Id
    {
        get
        {
            return Operation.Skill.ToString(CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Mapping files of the effect packs. Every line names the choice in its comment ("# name" or
/// "# name | description"); blank lines and lines starting with # are ignored.
/// <list type="bullet">
/// <item>effect swaps: "&lt;skill a&gt; &lt;skill b&gt;  # name", each skill in one pair only;</item>
/// <item>icons over the target: "&lt;skill id&gt; [seconds] [@&lt;effect class id&gt;]  # name", where @ names the
/// class the game plays for the skill when it is not its own l2_&lt;id&gt;_skill; each skill and class once.</item>
/// </list>
/// </summary>
public static class EffectMappings
{
    public static IReadOnlyList<EffectSwapEntry> LoadSwaps(string path)
    {
        return ParseSwaps(File.ReadAllLines(path));
    }

    public static IReadOnlyList<TargetIconEntry> LoadTargetIcons(string path)
    {
        return ParseTargetIcons(File.ReadAllLines(path));
    }

    public static IReadOnlyList<EffectSwapEntry> ParseSwaps(IReadOnlyList<string> lines)
    {
        var entries = new List<EffectSwapEntry>();
        var skills = new HashSet<int>();
        foreach (MappingLine line in MappingFile.Parse(lines, "<skill a> <skill b>"))
        {
            int first = ParseSkill(line.Key, line.LineNumber);
            int second = ParseSkill(line.Value, line.LineNumber);
            if (first == second || !skills.Add(first) || !skills.Add(second))
            {
                throw new FormatException($"Line {line.LineNumber}: every skill can take part in one swap only.");
            }

            entries.Add(new EffectSwapEntry(new EffectSwapOperation(first, second), OptionLabel.Parse(line.Label, line.LineNumber), line.LineNumber));
        }

        return entries;
    }

    public static IReadOnlyList<TargetIconEntry> ParseTargetIcons(IReadOnlyList<string> lines)
    {
        var entries = new List<TargetIconEntry>();
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

            if (parts.Length > 3)
            {
                throw new FormatException($"Line {lineNumber}: expected '<skill id> [seconds] [@<effect class id>]', got '{line.Trim()}'.");
            }

            int skill = ParseSkill(parts[0], lineNumber);
            int seconds = EffectKinds.DefaultIconSeconds;
            int effectClass = skill;
            foreach (string part in parts.Skip(1))
            {
                if (part.StartsWith('@'))
                {
                    effectClass = ParseSkill(part.Substring(1), lineNumber);
                }
                else if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    || seconds < EffectKinds.MinIconSeconds || seconds > EffectKinds.MaxIconSeconds)
                {
                    throw new FormatException($"Line {lineNumber}: '{part}' must be whole seconds from {EffectKinds.MinIconSeconds} to {EffectKinds.MaxIconSeconds}, or @<effect class id>.");
                }
            }

            if (entries.Any(entry => entry.Operation.Skill == skill || entry.Operation.EffectClass == effectClass))
            {
                throw new FormatException($"Line {lineNumber}: skill {skill} or effect class {effectClass} is listed twice.");
            }

            var operation = new TargetIconOperation(skill, effectClass, seconds);
            entries.Add(new TargetIconEntry(operation, OptionLabel.Parse(label, lineNumber), lineNumber));
        }

        if (entries.Count == 0)
        {
            throw new FormatException("Mapping file has no entries.");
        }

        return entries;
    }

    private static int ParseSkill(string text, int lineNumber)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || !EffectKinds.IsValidSkillId(id))
        {
            throw new FormatException($"Line {lineNumber}: '{text}' is not a skill id (1 to {EffectKinds.MaxSkillId}).");
        }

        return id;
    }
}
