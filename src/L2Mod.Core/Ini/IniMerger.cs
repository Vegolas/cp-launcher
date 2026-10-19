using System.Text;

namespace L2Mod.Core.Ini;

public sealed record IniValue(string Section, string Key, string Value);

public sealed record IniMergeResult(byte[] Bytes, IReadOnlyList<IniValue> Changed)
{
    public bool HasChanges
    {
        get
        {
            return Changed.Count > 0;
        }
    }
}

/// <summary>
/// Sets values in an INI file (Option.ini) and leaves every other byte alone: other lines, their order,
/// comments and line endings stay as they were. Section and key names match case-insensitively, as in UE2.
/// Existing keys keep their spelling; a missing key goes after the last non-blank line of its section; a
/// missing section is appended at the end. New lines use the file's line ending (CRLF for an empty file).
/// </summary>
public static class IniMerger
{
    public static IniMergeResult Merge(byte[] original, IReadOnlyList<IniValue> values)
    {
        List<IniLine> lines = Split(Encoding.Latin1.GetString(original));
        string newline = DetectNewline(lines);
        var changed = new List<IniValue>();
        foreach (IniValue value in values)
        {
            int header = FindSection(lines, value.Section);
            if (header < 0)
            {
                header = AppendSection(lines, value.Section, newline);
            }

            int end = FindSectionEnd(lines, header);
            bool found = false;
            bool modified = false;
            for (int i = header + 1; i < end; i++)
            {
                int equals = GetKeyEquals(lines[i].Content, value.Key);
                if (equals < 0)
                {
                    continue;
                }

                found = true;
                string content = lines[i].Content.Substring(0, equals + 1) + value.Value;
                if (content != lines[i].Content)
                {
                    lines[i] = lines[i] with { Content = content };
                    modified = true;
                }
            }

            if (!found)
            {
                InsertKey(lines, header, end, value, newline);
                modified = true;
            }

            if (modified)
            {
                changed.Add(value);
            }
        }

        var output = new StringBuilder();
        foreach (IniLine line in lines)
        {
            output.Append(line.Content);
            output.Append(line.Ending);
        }

        return new IniMergeResult(Encoding.Latin1.GetBytes(output.ToString()), changed);
    }

    private static List<IniLine> Split(string text)
    {
        var lines = new List<IniLine>();
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(new IniLine(text.Substring(start), string.Empty));
                break;
            }

            int contentEnd = newline;
            if (contentEnd > start && text[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            lines.Add(new IniLine(text.Substring(start, contentEnd - start), text.Substring(contentEnd, newline + 1 - contentEnd)));
            start = newline + 1;
        }

        return lines;
    }

    private static string DetectNewline(List<IniLine> lines)
    {
        foreach (IniLine line in lines)
        {
            if (line.Ending.Length > 0)
            {
                return line.Ending;
            }
        }

        return "\r\n";
    }

    private static int FindSection(List<IniLine> lines, string section)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string? name = GetSectionName(lines[i].Content);
            if (name is not null && string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSectionEnd(List<IniLine> lines, int header)
    {
        for (int i = header + 1; i < lines.Count; i++)
        {
            if (GetSectionName(lines[i].Content) is not null)
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static string? GetSectionName(string content)
    {
        string trimmed = content.Trim();
        int close = trimmed.IndexOf(']');
        if (!trimmed.StartsWith('[') || close < 0)
        {
            return null;
        }

        return trimmed.Substring(1, close - 1).Trim();
    }

    /// <summary>Index of the '=' when the line assigns <paramref name="key"/>, otherwise -1.</summary>
    private static int GetKeyEquals(string content, string key)
    {
        string trimmed = content.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('['))
        {
            return -1;
        }

        int equals = content.IndexOf('=');
        if (equals < 0 || !string.Equals(content.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return equals;
    }

    private static int AppendSection(List<IniLine> lines, string section, string newline)
    {
        if (lines.Count > 0)
        {
            EnsureEnding(lines, lines.Count - 1, newline);
            if (lines[lines.Count - 1].Content.Trim().Length > 0)
            {
                lines.Add(new IniLine(string.Empty, newline));
            }
        }

        lines.Add(new IniLine($"[{section}]", newline));
        return lines.Count - 1;
    }

    private static void InsertKey(List<IniLine> lines, int header, int end, IniValue value, string newline)
    {
        int last = header;
        for (int i = header + 1; i < end; i++)
        {
            if (lines[i].Content.Trim().Length > 0)
            {
                last = i;
            }
        }

        string ending = lines[last].Ending;
        if (ending.Length == 0)
        {
            EnsureEnding(lines, last, newline);
        }
        else
        {
            ending = newline;
        }

        lines.Insert(last + 1, new IniLine($"{value.Key}={value.Value}", ending));
    }

    private static void EnsureEnding(List<IniLine> lines, int index, string newline)
    {
        if (lines[index].Ending.Length == 0)
        {
            lines[index] = lines[index] with { Ending = newline };
        }
    }

    private sealed record IniLine(string Content, string Ending);
}
