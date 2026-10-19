using System.IO.Compression;

namespace L2Mod.Core.Publishing;

/// <summary>
/// The zip a leader publishes (a GitHub release asset): the files the manifest points to, its signature and the
/// manifest, nothing else. <see cref="Extract"/> runs in CI and treats the archive as untrusted: only
/// <see cref="DistLayout"/> paths, no duplicates, bounded size.
/// </summary>
public static class DistArchive
{
    /// <summary>A launcher download may be 64 MB; a dist holds one launcher plus small assets.</summary>
    public const long MaxExtractedBytes = 256L * 1024 * 1024;

    public const int MaxEntries = 4096;

    /// <summary>Writes the archive of a verified, signed dist folder; returns the entries in archive order.</summary>
    public static IReadOnlyList<string> Create(string distDirectory, DistVerification verification, string zipPath)
    {
        if (!verification.IsValid)
        {
            throw new InvalidOperationException("Only a verified dist folder can be archived.");
        }

        var entries = new List<string>(verification.ReferencedFiles)
        {
            DistLayout.SignatureFile,
            DistLayout.ManifestFile,
        };
        string fullZipPath = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullZipPath)!);
        using (var stream = new FileStream(fullZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (string entry in entries)
                {
                    archive.CreateEntryFromFile(DistLayout.GetFullPath(distDirectory, entry), entry, CompressionLevel.Optimal);
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Extracts an archive into a new or empty folder; returns the extracted paths, sorted. Anything a publish would
    /// not have written throws <see cref="InvalidDataException"/> (the folder may then hold part of the archive).
    /// </summary>
    public static IReadOnlyList<string> Extract(string zipPath, string directory, long maxExtractedBytes = MaxExtractedBytes)
    {
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"{root} is not empty.");
        }

        Directory.CreateDirectory(root);
        var extracted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"{zipPath} has {archive.Entries.Count} entries; a dist has at most {MaxEntries}.");
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.EndsWith('/') && entry.Length == 0)
            {
                // A folder entry from a zip tool; the folders are created from the file paths.
                continue;
            }

            CheckEntryName(name);
            if (!seen.Add(name))
            {
                throw new InvalidDataException($"{name} appears twice in the archive.");
            }

            string path = DistLayout.GetFullPath(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (Stream input = entry.Open())
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    total += CopyLimited(input, output, maxExtractedBytes - total, name);
                }
            }

            extracted.Add(name);
        }

        extracted.Sort(StringComparer.Ordinal);
        return extracted;
    }

    private static void CheckEntryName(string name)
    {
        bool dotSegment = name.Split('/').Any(segment => segment == "." || segment == "..");
        if (name.Contains('\\') || dotSegment || !DistLayout.IsBuildOutput(name))
        {
            throw new InvalidDataException($"{name} does not belong in a dist archive (only {DistLayout.ManifestFile}, {DistLayout.SignatureFile}, {DistLayout.AssetsFolder}/ and {DistLayout.LauncherFolder}/).");
        }
    }

    private static long CopyLimited(Stream input, Stream output, long remaining, string name)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > remaining)
            {
                throw new InvalidDataException($"The archive expands beyond the limit while extracting {name}.");
            }

            output.Write(buffer, 0, read);
        }

        return copied;
    }
}
