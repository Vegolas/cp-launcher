using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Publishing;

/// <summary>
/// Result of checking a build output folder. <see cref="ReferencedFiles"/> are the manifest-relative paths the
/// manifest points to (assets, launcher), in upload order.
/// </summary>
public sealed record DistVerification(Manifest? Manifest, IReadOnlyList<string> ReferencedFiles, IReadOnlyList<string> Errors)
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
/// Checks a dist folder the way a launcher will read it: the signature over the exact manifest bytes, the
/// manifest without any skipped part, and every referenced asset and launcher file against its hash and limits.
/// </summary>
public static class DistVerifier
{
    /// <param name="publicKeyPem">The leader's public key; null checks everything except the signature (before signing).</param>
    public static DistVerification Verify(string distDirectory, string? publicKeyPem)
    {
        var errors = new List<string>();
        var referenced = new List<string>();
        string manifestPath = Path.Combine(distDirectory, DistLayout.ManifestFile);
        if (!File.Exists(manifestPath))
        {
            errors.Add($"{manifestPath} not found.");
            return new DistVerification(null, referenced, errors);
        }

        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        ManifestReadResult result;
        try
        {
            if (publicKeyPem is null)
            {
                result = ManifestParser.Parse(manifestBytes);
            }
            else
            {
                string signaturePath = Path.Combine(distDirectory, DistLayout.SignatureFile);
                if (!File.Exists(signaturePath))
                {
                    errors.Add($"{signaturePath} not found; run sign first.");
                    return new DistVerification(null, referenced, errors);
                }

                result = ManifestSignature.ReadVerified(manifestBytes, File.ReadAllBytes(signaturePath), publicKeyPem);
            }
        }
        catch (ManifestException exception)
        {
            errors.Add(exception.Message);
            return new DistVerification(null, referenced, errors);
        }

        foreach (string message in result.Messages)
        {
            errors.Add($"The launcher would skip part of the manifest: {message}");
        }

        Manifest manifest = result.Manifest;
        foreach (OverlayOperation overlay in manifest.Packs.SelectMany(pack => pack.Operations).OfType<OverlayOperation>())
        {
            if (referenced.Contains(overlay.Image, StringComparer.Ordinal))
            {
                continue;
            }

            referenced.Add(overlay.Image);
            CheckAsset(distDirectory, overlay, errors);
        }

        if (manifest.Launcher is not null)
        {
            referenced.Add(manifest.Launcher.Url);
            CheckLauncher(distDirectory, manifest.Launcher, errors);
        }

        return new DistVerification(manifest, referenced, errors);
    }

    private static void CheckAsset(string distDirectory, OverlayOperation overlay, List<string> errors)
    {
        string path = DistLayout.GetFullPath(distDirectory, overlay.Image);
        if (!File.Exists(path))
        {
            errors.Add($"{overlay.Image} is missing.");
            return;
        }

        try
        {
            OverlayImages.Decode(File.ReadAllBytes(path), overlay.ImageSha256);
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"{overlay.Image}: {exception.Message}");
        }
    }

    private static void CheckLauncher(string distDirectory, LauncherRelease launcher, List<string> errors)
    {
        string path = DistLayout.GetFullPath(distDirectory, launcher.Url);
        if (!File.Exists(path))
        {
            errors.Add($"{launcher.Url} is missing.");
            return;
        }

        if (!ContentHash.Matches(launcher.Sha256, File.ReadAllBytes(path)))
        {
            errors.Add($"{launcher.Url} does not match its SHA-256 {launcher.Sha256}.");
        }
    }
}
