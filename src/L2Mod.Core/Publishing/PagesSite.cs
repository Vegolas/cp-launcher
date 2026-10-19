using L2Mod.Core.Manifests;

namespace L2Mod.Core.Publishing;

/// <summary>What <see cref="PagesSite.Stage"/> wrote: the files of this version and those kept from the previous one.</summary>
public sealed record PagesStageResult(
    Manifest? Manifest,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> KeptFromPrevious,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool IsValid
    {
        get
        {
            return Errors.Count == 0;
        }
    }
}

/// <summary>
/// Builds the GitHub Pages site from a published dist, in CI and without the private key: the dist must be signed
/// for the given public key (the one the launcher embeds) and hold nothing the manifest does not reference.
/// Every Pages deployment replaces the whole site while the CDN keeps serving manifest.json for up to 10 minutes,
/// so the files the previous manifest references are kept: a launcher that read it just before the deployment can
/// still download them. A file name both versions use must have the same bytes, because the CDN may still serve the
/// old ones.
/// </summary>
public static class PagesSite
{
    /// <param name="previousDirectory">The dist that is live now, or null for the first deployment.</param>
    public static PagesStageResult Stage(string distDirectory, string publicKeyPem, string? previousDirectory, string siteDirectory)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var kept = new List<string>();
        DistVerification current = DistVerifier.Verify(distDirectory, publicKeyPem);
        if (!current.IsValid)
        {
            return new PagesStageResult(current.Manifest, Array.Empty<string>(), kept, warnings, current.Errors);
        }

        foreach (string file in DistVerifier.FindUnreferenced(distDirectory, current))
        {
            errors.Add($"{file} is not referenced by the manifest; a published dist holds nothing else.");
        }

        if (previousDirectory is not null)
        {
            CollectPrevious(distDirectory, current, previousDirectory, kept, warnings, errors);
        }

        if (errors.Count > 0)
        {
            return new PagesStageResult(current.Manifest, Array.Empty<string>(), Array.Empty<string>(), warnings, errors);
        }

        string root = Path.GetFullPath(siteDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"{root} is not empty.");
        }

        var files = new List<string>(current.ReferencedFiles)
        {
            DistLayout.SignatureFile,
            DistLayout.ManifestFile,
        };
        foreach (string file in files)
        {
            Copy(distDirectory, root, file);
        }

        foreach (string file in kept)
        {
            Copy(previousDirectory!, root, file);
        }

        return new PagesStageResult(current.Manifest, files, kept, warnings, errors);
    }

    private static void CollectPrevious(
        string distDirectory,
        DistVerification current,
        string previousDirectory,
        List<string> kept,
        List<string> warnings,
        List<string> errors)
    {
        // Not checked against the key: the previous version may be signed by a key since replaced. Its files are
        // checked against its manifest, and launchers check them again.
        DistVerification previous = DistVerifier.Verify(previousDirectory, null);
        if (!previous.IsValid)
        {
            warnings.Add("The previous version's files are not kept: " + string.Join(" ", previous.Errors));
            return;
        }

        foreach (string file in previous.ReferencedFiles)
        {
            if (!current.ReferencedFiles.Contains(file, StringComparer.Ordinal))
            {
                kept.Add(file);
                continue;
            }

            byte[] now = File.ReadAllBytes(DistLayout.GetFullPath(distDirectory, file));
            byte[] before = File.ReadAllBytes(DistLayout.GetFullPath(previousDirectory, file));
            if (!now.AsSpan().SequenceEqual(before))
            {
                errors.Add($"{file} has different bytes than in the live version. The CDN may keep serving the old file under this name: give changed content a new name (for the launcher, a new version).");
            }
        }
    }

    private static void Copy(string fromDirectory, string toDirectory, string relativePath)
    {
        string target = DistLayout.GetFullPath(toDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(DistLayout.GetFullPath(fromDirectory, relativePath), target, false);
    }
}
