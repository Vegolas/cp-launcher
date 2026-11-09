using L2Mod.Core.Badges;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Recipes;

namespace L2Mod.Core.Publishing;

/// <summary>One operation of a pack plus a readable line for the preview index ("skill0271 &lt;- skill1068  # label").</summary>
/// <param name="Option">The option the operation belongs to, for a pack with options; null otherwise.</param>
public sealed record RecipeEntry(IPatchOperation Operation, string Summary, PackOption? Option = null);

/// <param name="Options">The choices of a pack the member ticks one by one (effect swaps, icons over the target);
/// empty for a pack selected as a whole.</param>
public sealed record CompiledPack(RecipePack Recipe, IReadOnlyList<RecipeEntry> Entries, IReadOnlyList<PackOption> Options)
{
    public CompiledPack(RecipePack recipe, IReadOnlyList<RecipeEntry> entries)
        : this(recipe, entries, Array.Empty<PackOption>())
    {
    }

    public IReadOnlyList<IPatchOperation> Operations
    {
        get
        {
            return Entries.Select(entry => entry.Operation).ToList();
        }
    }

    public Pack ToManifestPack()
    {
        if (Options.Count > 0)
        {
            return Pack.WithOptions(Recipe.Id, Recipe.Name, Recipe.Default, Options, Recipe.Description);
        }

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
/// Effect swaps and icons over the target become packs with one option per mapping line (<see cref="EffectMappings"/>),
/// each defaulting to the pack's default.
/// </summary>
public static class RecipeCompiler
{
    public static CompiledRecipes Compile(RecipeProject project)
    {
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var packs = new List<CompiledPack>();
        foreach (RecipePack pack in project.Packs)
        {
            if (pack.MappingPath.Length > 0 && !File.Exists(pack.MappingPath))
            {
                throw new FileNotFoundException($"Pack '{pack.Id}': mapping file {pack.MappingPath} not found.", pack.MappingPath);
            }

            try
            {
                packs.Add(CompilePack(pack, assets));
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Pack '{pack.Id}' ({pack.MappingPath}): {exception.Message}", exception);
            }
        }

        return new CompiledRecipes(packs, assets);
    }

    private static CompiledPack CompilePack(RecipePack pack, Dictionary<string, byte[]> assets)
    {
        switch (pack.Type)
        {
            case RecipePackType.Badge:
                return new CompiledPack(pack, CompileBadges(pack));
            case RecipePackType.Tag:
                return new CompiledPack(pack, CompileTags(pack));
            case RecipePackType.Overlay:
                return new CompiledPack(pack, CompileOverlays(pack, assets));
            case RecipePackType.GoldClickArrow:
                return new CompiledPack(pack, new[] { new RecipeEntry(new GoldClickArrowOperation(), "click-to-move arrow -> gold") });
            case RecipePackType.EffectSwap:
                return CompileSwaps(pack);
            default:
                return CompileTargetIcons(pack);
        }
    }

    /// <summary>One option per pair; the option id is "&lt;first&gt;-&lt;second&gt;", its name the line's label.</summary>
    private static CompiledPack CompileSwaps(RecipePack pack)
    {
        var entries = new List<RecipeEntry>();
        var options = new List<PackOption>();
        foreach (EffectSwapEntry line in EffectMappings.LoadSwaps(pack.MappingPath))
        {
            var option = new PackOption(line.Id, line.Label.Name, pack.Default, new IPatchOperation[] { line.Operation }, line.Label.Description);
            options.Add(option);
            entries.Add(new RecipeEntry(line.Operation, $"{line.Operation.First} <-> {line.Operation.Second}  # {line.Label.Name}", option));
        }

        return new CompiledPack(pack, entries, options);
    }

    /// <summary>One option per skill; the option id is the skill id, its name the line's label.</summary>
    private static CompiledPack CompileTargetIcons(RecipePack pack)
    {
        var entries = new List<RecipeEntry>();
        var options = new List<PackOption>();
        foreach (TargetIconEntry line in EffectMappings.LoadTargetIcons(pack.MappingPath))
        {
            TargetIconOperation operation = line.Operation;
            var option = new PackOption(line.Id, line.Label.Name, pack.Default, new IPatchOperation[] { operation }, line.Label.Description);
            options.Add(option);
            string summary = $"{operation.Skill} @{operation.EffectClass} {operation.Seconds}s  # {line.Label.Name}";
            entries.Add(new RecipeEntry(operation, summary, option));
        }

        return new CompiledPack(pack, entries, options);
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
