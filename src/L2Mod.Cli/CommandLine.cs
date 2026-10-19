namespace L2Mod.Cli;

/// <summary>Positional arguments plus "--name value" options and "--flag" switches.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private CommandLine(List<string> positional, Dictionary<string, string> options, HashSet<string> flags)
    {
        Positional = positional;
        _options = options;
        _flags = flags;
    }

    public IReadOnlyList<string> Positional { get; }

    /// <param name="valueOptions">Option names (without "--") that take a value.</param>
    /// <param name="flagOptions">Option names (without "--") that are plain switches.</param>
    public static CommandLine Parse(IReadOnlyList<string> arguments, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            string name = argument.Substring(2);
            if (flagOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                flags.Add(name);
            }
            else if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= arguments.Count)
                {
                    throw new ArgumentException($"Option --{name} needs a value.");
                }

                options[name] = arguments[i + 1];
                i++;
            }
            else
            {
                throw new ArgumentException($"Unknown option --{name}.");
            }
        }

        return new CommandLine(positional, options, flags);
    }

    public bool HasFlag(string name)
    {
        return _flags.Contains(name);
    }

    public string? GetOption(string name)
    {
        if (_options.TryGetValue(name, out string? value))
        {
            return value;
        }

        return null;
    }

    public int GetInt(string name, int defaultValue, int minimum, int maximum)
    {
        string? value = GetOption(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException($"--{name} must be a number between {minimum} and {maximum}.");
        }

        return parsed;
    }
}
