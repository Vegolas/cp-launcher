using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;

namespace CpLauncher.Services;

public sealed record PreviewIcon(string Target, RgbaImage Image);

/// <summary>A few icons of a pack as they will look; <see cref="Failed"/> counts operations that could not be drawn.</summary>
public sealed record PackPreview(string PackId, IReadOnlyList<PreviewIcon> Icons, int Failed);

/// <summary>
/// Renders pack thumbnails locally from the member's own Icon.utx, so nothing from NCSoft is distributed.
/// Operations are drawn on the original (the backup, when our patch is installed), never on a patched file,
/// where badges would stack.
/// </summary>
public sealed class PackPreviewRenderer
{
    public const int MaxIconsPerPack = 6;

    private readonly IFileSystem _fileSystem;

    public PackPreviewRenderer(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>The backed-up original when there is a valid one, otherwise the client file; null when neither exists.</summary>
    public byte[]? ReadOriginal(InstallerOptions options)
    {
        try
        {
            InstalledFile? installed = new InstallStateStore(_fileSystem, options.StatePath).Load().Get(ClientFiles.IconUtx);
            if (installed is not null)
            {
                string backup = options.GetBackupPath(ClientFiles.IconUtx, installed.OriginalSha256);
                if (_fileSystem.FileExists(backup))
                {
                    byte[] bytes = _fileSystem.ReadAllBytes(backup);
                    if (ContentHash.Matches(installed.OriginalSha256, bytes))
                    {
                        return bytes;
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            // A damaged state.json is reported by the installer; the preview just uses the client file.
        }

        string clientPath = options.GetClientPath(ClientFiles.IconUtx);
        if (!_fileSystem.FileExists(clientPath))
        {
            return null;
        }

        return _fileSystem.ReadAllBytes(clientPath);
    }

    public IReadOnlyList<PackPreview> Render(byte[] iconUtx, Manifest manifest, IAssetStore assets)
    {
        IconPackage package;
        try
        {
            package = new IconPackage(Path.GetFileNameWithoutExtension(ClientFiles.IconUtx), L2File.FromBytes(iconUtx));
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return manifest.Packs.Select(pack => new PackPreview(pack.Id, Array.Empty<PreviewIcon>(), pack.Operations.Count)).ToList();
        }

        var previews = new List<PackPreview>();
        foreach (Pack pack in manifest.Packs)
        {
            previews.Add(RenderPack(package, pack, assets));
        }

        return previews;
    }

    private static PackPreview RenderPack(IconPackage package, Pack pack, IAssetStore assets)
    {
        var icons = new List<PreviewIcon>();
        int failed = 0;
        foreach (IGrouping<string, IIconOperation> target in GroupByTarget(pack))
        {
            if (icons.Count >= MaxIconsPerPack)
            {
                break;
            }

            try
            {
                var warnings = new List<string>();
                IReadOnlyList<ComposedTexture> composed = OperationComposer.Compose(package, target.ToList(), assets, warnings);
                foreach (ComposedTexture texture in composed)
                {
                    icons.Add(new PreviewIcon(target.Key, texture.After));
                }
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or InvalidDataException
                or FileNotFoundException or ArgumentException or NotSupportedException)
            {
                failed++;
            }
        }

        return new PackPreview(pack.Id, icons, failed);
    }

    private static IEnumerable<IGrouping<string, IIconOperation>> GroupByTarget(Pack pack)
    {
        return pack.Operations
            .Where(operation => string.Equals(operation.File, ClientFiles.IconUtx, StringComparison.OrdinalIgnoreCase))
            .GroupBy(operation => operation.Target, StringComparer.OrdinalIgnoreCase);
    }
}
