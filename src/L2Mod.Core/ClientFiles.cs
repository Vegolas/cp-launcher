namespace L2Mod.Core;

/// <summary>The only client files the launcher may change, relative to the client folder.</summary>
public static class ClientFiles
{
    public const string IconUtx = "Icon.utx";

    private static readonly Dictionary<string, string> PatchablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [IconUtx] = Path.Combine("SysTextures", IconUtx),
    };

    /// <summary>Whitelisted texture packages (file name as used in manifests).</summary>
    public static IReadOnlyCollection<string> Patchable
    {
        get
        {
            return new[] { IconUtx };
        }
    }

    public static string OptionIniPath
    {
        get
        {
            return Path.Combine("system_season", "Option.ini");
        }
    }

    public static bool IsPatchable(string fileName)
    {
        return PatchablePaths.ContainsKey(fileName);
    }

    /// <summary>The whitelisted spelling of <paramref name="fileName"/> (manifest names are case-insensitive).</summary>
    public static string GetCanonicalName(string fileName)
    {
        foreach (string name in Patchable)
        {
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        throw new ArgumentException($"{fileName} is not a patchable client file.");
    }

    public static string GetRelativePath(string fileName)
    {
        if (!PatchablePaths.TryGetValue(fileName, out string? path))
        {
            throw new ArgumentException($"{fileName} is not a patchable client file.");
        }

        return path;
    }
}
