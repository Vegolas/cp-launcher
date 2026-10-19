using System.Text.RegularExpressions;

namespace L2Mod.Cli.Leader;

/// <summary>
/// The GitHub repository whose releases carry the published dists and whose Pages serve them, plus the names
/// <c>publish</c> and <c>.github/workflows/pages.yml</c> agree on.
/// </summary>
public sealed partial record PagesTarget(string Owner, string Name)
{
    public const string Workflow = "pages.yml";
    public const string TagPrefix = "dist-";

    public string Repository
    {
        get
        {
            return $"{Owner}/{Name}";
        }
    }

    /// <summary>The project site, e.g. https://vegolas.github.io/cp-launcher/ (the launcher's built-in source).</summary>
    public Uri SiteUri
    {
        get
        {
            return new Uri($"https://{Owner.ToLowerInvariant()}.github.io/{Name}/");
        }
    }

    /// <summary>"owner/name".</summary>
    public static PagesTarget Parse(string repository)
    {
        Match match = RepositoryPattern().Match(repository);
        if (!match.Success)
        {
            throw new ArgumentException($"'{repository}' is not a GitHub repository (owner/name).");
        }

        return new PagesTarget(match.Groups[1].Value, match.Groups[2].Value);
    }

    /// <summary>The release tag of a manifest version: one release per version, never replaced.</summary>
    public static string GetTag(string manifestVersion)
    {
        return TagPrefix + manifestVersion;
    }

    [GeneratedRegex("^([A-Za-z0-9-]+)/([A-Za-z0-9._-]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}
