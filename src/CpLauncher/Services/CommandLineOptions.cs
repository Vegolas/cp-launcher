namespace CpLauncher.Services;

/// <summary>
/// Development and test switches; members start the exe without arguments.
/// <list type="bullet">
/// <item><c>--data &lt;dir&gt;</c>: data folder instead of %LocalAppData%\CpLauncher.</item>
/// <item><c>--client &lt;dir&gt;</c> / <c>--source &lt;url|dir&gt;</c>: replace the stored client folder / manifest source.</item>
/// <item><c>--dev-public-key &lt;pem&gt;</c>: also trust this key (a warning stays visible while it is used).</item>
/// <item><c>--smoke &lt;report&gt;</c>: apply and restore the selected packs, write a report (plus PNGs of the window) and exit.</item>
/// </list>
/// </summary>
public sealed record CommandLineOptions(
    string? DataDirectory,
    string? ClientDirectory,
    string? ManifestSource,
    string? DevPublicKeyPath,
    string? SmokeReportPath,
    IReadOnlyList<string> Problems)
{
    private static readonly string[] Switches = { "--data", "--client", "--source", "--dev-public-key", "--smoke" };

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            if (!Switches.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Nieznany argument: {name}");
                continue;
            }

            if (i + 1 >= args.Count)
            {
                problems.Add($"Brak wartości dla {name}");
                continue;
            }

            values[name] = args[i + 1];
            i++;
        }

        return new CommandLineOptions(
            Get(values, "--data"),
            Get(values, "--client"),
            Get(values, "--source"),
            Get(values, "--dev-public-key"),
            Get(values, "--smoke"),
            problems);
    }

    private static string? Get(Dictionary<string, string> values, string name)
    {
        if (values.TryGetValue(name, out string? value))
        {
            return value;
        }

        return null;
    }
}
