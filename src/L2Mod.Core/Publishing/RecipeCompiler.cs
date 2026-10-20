using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Recipes;

namespace L2Mod.Core.Publishing;

/// <summary>One operation of a pack plus a readable line for the preview index ("skill0271 &lt;- skill1068  # label").</summary>
public sealed record RecipeEntry(IIconOperation Operation, string Summary);

public sealed record CompiledPack(RecipePack Recipe, IReadOnlyList<RecipeEntry> Entries)
{
    public IReadOnlyList<IIconOperation> Operations
    {
        get
        {
            return Entries.Select(entry => entry.Operation).ToList();
        }
    }

    public Pack ToManifestPack()
    {
        return new Pack(Recipe.Id, Recipe.Name, Recipe.Default, Operations, Recipe.Description);
    }
}

/// <summary>Packs as operations, plus the overlay PNGs by manifest path ("assets/&lt;sha256&gt;.png").</summary>
public sealed record CompiledRecipes(IReadOnlyList<CompiledPack> Packs, IReadOnlyDictionary<string, byte[]> Assets);

/// <summary>
/// Turns the packs of a <see cref="RecipeProject"/> into operations. Mapping files use the shared line format
/// (<see cref="MappingFile"/>): badge "target badge", tag "target category", overlay "target image.png" with the
/// PNG path relative to the mapping file. Overlay PNGs are checked against the launcher's limits and re-encoded
/// with <see cref="PngCodec"/>, which drops editor metadata and guarantees the launcher can decode them.
/// </summary>
public static class RecipeCompiler
{
    public static CompiledRecipes Compile(RecipeProject project)
    {
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var packs = new List<CompiledPack>();
        foreach (RecipePack pack in project.Packs)
        {
            if (!File.Exists(pack.MappingPath))
            {
                throw new FileNotFoundException($"Pack '{pack.Id}': mapping file {pack.MappingPath} not found.", pack.MappingPath);
            }

            try
            {
                packs.Add(new CompiledPack(pack, CompilePack(pack, assets)));
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Pack '{pack.Id}' ({pack.MappingPath}): {exception.Message}", exception);
            }
        }

        return new CompiledRecipes(packs, assets);
    }

    private static IReadOnlyList<RecipeEntry> CompilePack(RecipePack pack, Dictionary<string, byte[]> assets)
    {
        switch (pack.Type)
        {
            case RecipePackType.Badge:
                return CompileBadges(pack);
            case RecipePackType.Tag:
                return CompileTags(pack);
            default:
                return CompileOverlays(pack, assets);
        }
    }

    private static IReadOnlyList<RecipeEntry> CompileBadges(RecipePack pack)
    {
        BadgeStyle style = RecipeDefaults.BadgeStyle;
        if (pack.Size is int size)
        {
            style = style with { Size = size };
        }

        if (pack.Corner is IconCorner corner)
        {
            style = style with { Corner = corner };
        }

        IReadOnlyList<BadgeMappingEntry> mapping = BadgeMapping.Load(pack.MappingPath);
        IReadOnlyList<BadgeOperation> operations = MappingOperations.FromBadges(mapping, RecipeDefaults.IconFile, style);
        var entries = new List<RecipeEntry>();
        for (int i = 0; i < mapping.Count; i++)
        {
            entries.Add(new RecipeEntry(operations[i], Summarize($"{mapping[i].Target} <- {mapping[i].Badge}", mapping[i].Label)));
        }

        return entries;
    }

    private static IReadOnlyList<RecipeEntry> CompileTags(RecipePack pack)
    {
        IReadOnlyList<TagMappingEntry> mapping = TagMapping.Load(pack.MappingPath);
        IReadOnlyList<TagOperation> operations = MappingOperations.FromTags(
            mapping,
            RecipeDefaults.IconFile,
            pack.Colors ?? RecipeDefaults.DebuffColors,
            pack.Size ?? RecipeDefaults.TagSize,
            pack.Corner ?? RecipeDefaults.TagCorner);
        var entries = new List<RecipeEntry>();
        for (int i = 0; i < mapping.Count; i++)
        {
            entries.Add(new RecipeEntry(operations[i], Summarize($"{mapping[i].Target} {mapping[i].Category} {operations[i].Color}", mapping[i].Label)));
        }

        return entries;
    }

    private static IReadOnlyList<RecipeEntry> CompileOverlays(RecipePack pack, Dictionary<string, byte[]> assets)
    {
        string directory = Path.GetDirectoryName(pack.MappingPath)!;
        IconCorner corner = pack.Corner ?? OverlayOperation.DefaultCorner;
        var entries = new List<RecipeEntry>();
        foreach (MappingLine line in MappingFile.Parse(File.ReadAllLines(pack.MappingPath), "<target> <image.png>"))
        {
            string imagePath = Path.GetFullPath(Path.Combine(directory, line.Value));
            byte[] png = NormalizeOverlay(imagePath);
            string sha256 = ContentHash.Sha256Hex(png);
            string assetPath = $"{DistLayout.AssetsFolder}/{sha256}.png";
            assets[assetPath] = png;
            var operation = new OverlayOperation(RecipeDefaults.IconFile, line.Key, assetPath, sha256, corner);
            entries.Add(new RecipeEntry(operation, Summarize($"{line.Key} <- {line.Value}", line.Label)));
        }

        return entries;
    }

    /// <summary>Checks the source PNG against the launcher's limits and returns it re-encoded as 8-bit RGBA.</summary>
    public static byte[] NormalizeOverlay(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Overlay image {imagePath} not found.", imagePath);
        }

        try
        {
            byte[] source = File.ReadAllBytes(imagePath);
            RgbaImage image = OverlayImages.Decode(source, ContentHash.Sha256Hex(source));
            byte[] normalized = PngCodec.Encode(image);
            OverlayImages.Decode(normalized, ContentHash.Sha256Hex(normalized));
            return normalized;
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{imagePath}: {exception.Message}", exception);
        }
    }

    private static string Summarize(string text, string label)
    {
        if (label.Length == 0)
        {
            return text;
        }

        return $"{text}  # {label}";
    }
}
