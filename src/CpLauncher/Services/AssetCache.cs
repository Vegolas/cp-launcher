using L2Mod.Core;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;

namespace CpLauncher.Services;

/// <summary>Missing lists overlay images that are neither cached nor downloadable; Errors are images that failed checks.</summary>
public sealed record AssetSyncResult(IReadOnlyList<string> Missing, IReadOnlyList<string> Errors)
{
    public bool IsComplete
    {
        get
        {
            return Missing.Count == 0 && Errors.Count == 0;
        }
    }
}

/// <summary>
/// Overlay images in cache\assets\&lt;sha256&gt;.png, read by Core's <see cref="DirectoryAssetStore"/>. A download is
/// kept only when it matches the SHA-256 from the signed manifest and the size limit.
/// </summary>
public sealed class AssetCache
{
    private readonly IFileSystem _fileSystem;
    private readonly LauncherPaths _paths;

    public AssetCache(IFileSystem fileSystem, LauncherPaths paths)
    {
        _fileSystem = fileSystem;
        _paths = paths;
        Store = new DirectoryAssetStore(fileSystem, paths.CacheDirectory);
    }

    public IAssetStore Store { get; }

    public static IReadOnlyList<OverlayOperation> GetOverlays(Manifest manifest, IReadOnlyCollection<string> packIds)
    {
        var overlays = new List<OverlayOperation>();
        foreach (Pack pack in manifest.Packs)
        {
            if (!packIds.Contains(pack.Id))
            {
                continue;
            }

            foreach (IIconOperation operation in pack.Operations)
            {
                if (operation is OverlayOperation overlay)
                {
                    overlays.Add(overlay);
                }
            }
        }

        return overlays;
    }

    public bool IsCached(OverlayOperation overlay)
    {
        string path = _paths.GetCachedAssetPath(overlay.Image);
        return _fileSystem.FileExists(path) && ContentHash.Matches(overlay.ImageSha256, _fileSystem.ReadAllBytes(path));
    }

    /// <summary>Downloads the images that are not cached yet; <paramref name="source"/> null means offline.</summary>
    public async Task<AssetSyncResult> EnsureAsync(IReadOnlyList<OverlayOperation> overlays, IManifestSource? source, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var errors = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayOperation overlay in overlays)
        {
            if (!done.Add(overlay.ImageSha256) || IsCached(overlay))
            {
                continue;
            }

            if (source is null)
            {
                missing.Add(overlay.Image);
                continue;
            }

            try
            {
                byte[] bytes = await source.ReadAsync(overlay.Image, OverlayImages.MaxBytes, cancellationToken);
                if (!ContentHash.Matches(overlay.ImageSha256, bytes))
                {
                    errors.Add($"{overlay.Image}: plik nie zgadza się z podpisanym manifestem.");
                    continue;
                }

                VerifiedWrite.Write(_fileSystem, _paths.GetCachedAssetPath(overlay.Image), bytes);
            }
            catch (SourceUnavailableException)
            {
                missing.Add(overlay.Image);
            }
            catch (Exception exception) when (exception is ManifestException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{overlay.Image}: {exception.Message}");
            }
        }

        return new AssetSyncResult(missing, errors);
    }
}
