using L2Mod.Core.Imaging;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Operations;

/// <summary>A texture after all its operations; <see cref="Regions"/> are the rectangles to re-encode.</summary>
public sealed record ComposedTexture(
    TextureData Texture,
    RgbaImage Before,
    RgbaImage After,
    IReadOnlyList<PixelRect> Regions,
    IReadOnlyList<IIconOperation> Operations);

public sealed record OperationBuildResult(
    ReplacementResult Replacement,
    IReadOnlyList<ComposedTexture> Textures,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies operations to one package: operations are grouped by texture (in order of first appearance),
/// drawn in order onto one RGBA image per texture and encoded once, re-encoding only the touched blocks.
/// </summary>
public static class OperationComposer
{
    public static OperationBuildResult Build(IconPackage package, IReadOnlyList<IIconOperation> operations, IAssetStore? assets)
    {
        var warnings = new List<string>();
        IReadOnlyList<ComposedTexture> textures = Compose(package, operations, assets, warnings);
        var edits = new List<TextureEdit>();
        foreach (ComposedTexture texture in textures)
        {
            edits.Add(new TextureEdit(texture.Texture, texture.After, texture.Regions));
        }

        ReplacementResult replacement = package.BuildBatch(edits);
        package.VerifyReplacement(replacement, textures.Select(texture => texture.Texture.Path));
        warnings.AddRange(replacement.Warnings);
        return new OperationBuildResult(replacement, textures, warnings);
    }

    public static IReadOnlyList<ComposedTexture> Compose(
        IconPackage package,
        IReadOnlyList<IIconOperation> operations,
        IAssetStore? assets,
        ICollection<string> warnings)
    {
        string fileName = package.PackageName + ".utx";
        var groups = new List<(TextureData Texture, List<IIconOperation> Operations)>();
        var groupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IIconOperation operation in operations)
        {
            if (!string.Equals(operation.File, fileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{operation.Describe()}' is for {operation.File}, but the package is {fileName}.");
            }

            TextureData texture = package.FindTexture(operation.Target);
            if (!groupIndex.TryGetValue(texture.Path, out int index))
            {
                index = groups.Count;
                groupIndex[texture.Path] = index;
                groups.Add((texture, new List<IIconOperation>()));
            }

            groups[index].Operations.Add(operation);
        }

        var sources = new PackageSources(package, assets);
        var result = new List<ComposedTexture>();
        foreach ((TextureData texture, List<IIconOperation> textureOperations) in groups)
        {
            RgbaImage before = package.Decode(texture);
            RgbaImage after = before.Clone();
            var regions = new List<PixelRect>();
            var applied = new List<(IIconOperation Operation, PixelRect Rect)>();
            foreach (IIconOperation operation in textureOperations)
            {
                PixelRect? rect = operation.Apply(after, sources);
                if (rect is not PixelRect changed)
                {
                    warnings.Add($"{texture.Path}: '{operation.Describe()}' changed nothing.");
                    continue;
                }

                WarnAboutConflicts(texture.Path, operation, changed, applied, warnings);
                applied.Add((operation, changed));
                regions.Add(changed);
            }

            result.Add(new ComposedTexture(texture, before, after, regions, textureOperations));
        }

        return result;
    }

    private static void WarnAboutConflicts(
        string texturePath,
        IIconOperation operation,
        PixelRect rect,
        List<(IIconOperation Operation, PixelRect Rect)> earlier,
        ICollection<string> warnings)
    {
        foreach ((IIconOperation previous, PixelRect previousRect) in earlier)
        {
            if (previous.Corner == operation.Corner)
            {
                warnings.Add($"{texturePath}: {previous.Kind} and {operation.Kind} both use corner {IconCorners.ToCode(operation.Corner)}; the {operation.Kind} draws over the {previous.Kind}.");
            }
            else if (previousRect.Intersects(rect))
            {
                warnings.Add($"{texturePath}: {operation.Kind} ({IconCorners.ToCode(operation.Corner)}) overlaps {previous.Kind} ({IconCorners.ToCode(previous.Corner)}).");
            }
        }
    }

    private sealed class PackageSources : IOperationSources
    {
        private readonly IconPackage _package;
        private readonly IAssetStore? _assets;
        private readonly Dictionary<string, RgbaImage> _textures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RgbaImage> _overlays = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);

        public PackageSources(IconPackage package, IAssetStore? assets)
        {
            _package = package;
            _assets = assets;
        }

        public RgbaImage DecodeOriginal(string textureName)
        {
            TextureData texture = _package.FindTexture(textureName);
            if (!_textures.TryGetValue(texture.Path, out RgbaImage? image))
            {
                image = _package.Decode(texture);
                _textures[texture.Path] = image;
            }

            return image;
        }

        public RgbaImage LoadOverlay(string image, string sha256)
        {
            if (_assets is null)
            {
                throw new InvalidOperationException($"Overlay {image} needs an asset store.");
            }

            if (!_overlays.TryGetValue(sha256, out RgbaImage? overlay))
            {
                overlay = OverlayImages.Decode(_assets.ReadAsset(image), sha256);
                _overlays[sha256] = overlay;
            }

            return overlay;
        }
    }
}
