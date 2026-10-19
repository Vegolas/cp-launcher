using L2Mod.Core.Manifests;

namespace L2Mod.Core.Publishing;

public sealed record DistWriteResult(IReadOnlyList<string> Written, IReadOnlyList<string> Removed, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a build into a folder. The folder must be new, empty, or hold only files of an earlier build
/// (<see cref="DistLayout.IsBuildOutput"/>); those are replaced only with <c>overwrite</c>, including the old
/// signature. Any other file makes it refuse, so a wrong path (say, the client folder) is never written to.
/// The manifest is written last.
/// </summary>
public static class DistWriter
{
    public static DistWriteResult Write(string directory, DistBuildResult build, bool overwrite)
    {
        string root = Path.GetFullPath(directory);
        var warnings = new List<string>();
        var removed = new List<string>();
        if (Directory.Exists(root))
        {
            List<string> existing = ListFiles(root);
            if (existing.Count > 0)
            {
                CheckReplaceable(root, existing, overwrite);
                WarnAboutUnchangedVersion(root, build, warnings);
                foreach (string relative in existing)
                {
                    File.Delete(DistLayout.GetFullPath(root, relative));
                    removed.Add(relative);
                }
            }
        }

        var written = new List<string>();
        foreach (DistFile file in build.Files)
        {
            WriteFile(root, file.Path, file.Bytes);
            written.Add(file.Path);
        }

        WriteFile(root, DistLayout.ManifestFile, build.ManifestBytes);
        written.Add(DistLayout.ManifestFile);
        return new DistWriteResult(written, removed, warnings);
    }

    /// <summary>Files under <paramref name="root"/> as relative paths with '/' separators, sorted.</summary>
    public static List<string> ListFiles(string root)
    {
        var files = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void CheckReplaceable(string root, List<string> existing, bool overwrite)
    {
        List<string> foreign = existing.Where(path => !DistLayout.IsBuildOutput(path)).ToList();
        if (foreign.Count > 0)
        {
            string sample = string.Join(", ", foreign.Take(5));
            throw new IOException($"{root} is not a build output folder (it has {sample}); choose an empty or new folder.");
        }

        if (!overwrite)
        {
            throw new IOException($"{root} already holds a build. Pass --overwrite to replace it (the old signature is removed too).");
        }
    }

    private static void WarnAboutUnchangedVersion(string root, DistBuildResult build, List<string> warnings)
    {
        string path = Path.Combine(root, DistLayout.ManifestFile);
        if (!File.Exists(path))
        {
            return;
        }

        byte[] previous = File.ReadAllBytes(path);
        if (previous.AsSpan().SequenceEqual(build.ManifestBytes))
        {
            return;
        }

        try
        {
            if (ManifestParser.Parse(previous).Manifest.Version == build.Manifest.Version)
            {
                warnings.Add($"The manifest changed but its version is still {build.Manifest.Version}; bump \"version\" in recipes.json (or pass --version) so members can tell the builds apart.");
            }
        }
        catch (ManifestException)
        {
            // The old manifest was broken; it is replaced anyway.
        }
    }

    private static void WriteFile(string root, string relativePath, byte[] bytes)
    {
        string path = DistLayout.GetFullPath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
