using System.Text.RegularExpressions;

namespace L2Mod.Core.Publishing;

/// <summary>
/// Layout of a build output folder (dist/). Everything in it is meant for upload; preview sheets show
/// NCSoft icons and are written elsewhere.
/// <code>
/// manifest.json            schema 1, deterministic (ManifestWriter)
/// manifest.json.sig        detached ECDSA P-256 signature (sign)
/// assets/&lt;sha256&gt;.png     overlay images, content-addressed
/// launcher/&lt;name&gt;-&lt;version&gt;.exe   optional launcher release
/// </code>
/// </summary>
public static partial class DistLayout
{
    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "manifest.json.sig";
    public const string AssetsFolder = "assets";
    public const string LauncherFolder = "launcher";

    /// <summary>True for the files a build or sign writes (relative path with '/' separators).</summary>
    public static bool IsBuildOutput(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (path == ManifestFile || path == SignatureFile)
        {
            return true;
        }

        return AssetPattern().IsMatch(path) || LauncherPattern().IsMatch(path);
    }

    /// <summary>Absolute path of a manifest-relative path ("assets/x.png") inside <paramref name="distDirectory"/>.</summary>
    public static string GetFullPath(string distDirectory, string relativePath)
    {
        string root = Path.GetFullPath(distDirectory);
        string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{relativePath}' points outside {root}.");
        }

        return full;
    }

    [GeneratedRegex("^assets/[0-9a-f]{64}\\.png\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();

    [GeneratedRegex("^launcher/[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LauncherPattern();
}
