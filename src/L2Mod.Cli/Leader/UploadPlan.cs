using L2Mod.Core.Publishing;

namespace L2Mod.Cli.Leader;

public sealed record UploadStep(string Path, string ContentType, string CacheControl);

/// <summary>
/// Order and HTTP headers for uploading a verified dist folder. Referenced files go first with a long immutable
/// cache (their names carry a hash or a version); the signature and then the manifest go last with a short
/// cache, so members never get a manifest whose files are not online yet.
/// </summary>
public static class UploadPlan
{
    public const string ImmutableCache = "public, max-age=31536000, immutable";
    public const string ManifestCache = "public, max-age=60";

    public static IReadOnlyList<UploadStep> Create(DistVerification verification)
    {
        if (!verification.IsValid)
        {
            throw new InvalidOperationException("Only a verified dist folder can be uploaded.");
        }

        var steps = new List<UploadStep>();
        foreach (string path in verification.ReferencedFiles)
        {
            steps.Add(new UploadStep(path, GetContentType(path), ImmutableCache));
        }

        steps.Add(new UploadStep(DistLayout.SignatureFile, "application/octet-stream", ManifestCache));
        steps.Add(new UploadStep(DistLayout.ManifestFile, "application/json", ManifestCache));
        return steps;
    }

    /// <summary>The wrangler call for one step (Cloudflare R2; wrangler 4 needs --remote to leave local storage).</summary>
    public static string ToWranglerCommand(UploadStep step, string distDirectory, string bucket)
    {
        string file = DistLayout.GetFullPath(distDirectory, step.Path);
        return $"wrangler r2 object put \"{bucket}/{step.Path}\" --file \"{file}\" --content-type \"{step.ContentType}\" --cache-control \"{step.CacheControl}\" --remote";
    }

    private static string GetContentType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                return "image/png";
            case ".exe":
                return "application/vnd.microsoft.portable-executable";
            default:
                return "application/octet-stream";
        }
    }
}
