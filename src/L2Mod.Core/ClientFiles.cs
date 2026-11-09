namespace L2Mod.Core;

/// <summary>The only client files the launcher may change, relative to the client folder.</summary>
public static class ClientFiles
{
    public const string IconUtx = "Icon.utx";

    /// <summary>Skill effect classes (system_season); effect swaps and icons over the target.</summary>
    public const string LineageSkillEffect = "LineageSkillEffect.u";

    /// <summary>General effects (system_season); the click-to-move arrow.</summary>
    public const string LineageEffect = "LineageEffect.u";

    private static readonly Dictionary<string, string> PatchablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [IconUtx] = Path.Combine("SysTextures", IconUtx),
        [LineageSkillEffect] = Path.Combine("system_season", LineageSkillEffect),
        [LineageEffect] = Path.Combine("system_season", LineageEffect),
    };

    /// <summary>
    /// Whitelisted files (file name as used in manifests), in the order the installer handles them. Icon.utx comes
    /// first: the icons over the target in LineageSkillEffect.u are read from its original.
    /// </summary>
    public static IReadOnlyCollection<string> Patchable
    {
        get
        {
            return new[] { IconUtx, LineageSkillEffect, LineageEffect };
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

    /// <summary>True for the texture package whose icons the <c>IIconOperation</c>s draw on.</summary>
    public static bool IsIconPackage(string fileName)
    {
        return string.Equals(fileName, IconUtx, StringComparison.OrdinalIgnoreCase);
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
