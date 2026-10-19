using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Recipes;

/// <summary>Styles approved for the CP packs (2026-10-19).</summary>
public static class RecipeDefaults
{
    public const string IconFile = "Icon.utx";
    public const int BadgeSize = 20;
    public const int TagSize = TagOperation.DefaultSize;
    public const IconCorner TagCorner = TagOperation.DefaultCorner;

    /// <summary>20 px badge with a 1 px black border, bottom-right.</summary>
    public static BadgeStyle BadgeStyle
    {
        get
        {
            return new BadgeStyle(BadgeSize, IconCorner.BottomRight, 1);
        }
    }

    /// <summary>Debuff tag colours by category: CC red, DOT purple, STAT orange.</summary>
    public static IReadOnlyDictionary<string, Rgb> DebuffColors
    {
        get
        {
            return new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase)
            {
                ["CC"] = Rgb.ParseHex("#E02020"),
                ["DOT"] = Rgb.ParseHex("#A040FF"),
                ["STAT"] = Rgb.ParseHex("#FF9900"),
            };
        }
    }
}

/// <summary>Turns the mapping files (mappings/*.txt) into operations, ready to be compiled into a manifest.</summary>
public static class MappingOperations
{
    /// <summary>dances-songs.txt or chants.txt with the approved badge style.</summary>
    public static IReadOnlyList<BadgeOperation> LoadBadges(string path)
    {
        return FromBadges(BadgeMapping.Load(path), RecipeDefaults.IconFile, RecipeDefaults.BadgeStyle);
    }

    /// <summary>debuff-tags.txt with the approved tag size, corner and category colours.</summary>
    public static IReadOnlyList<TagOperation> LoadDebuffTags(string path)
    {
        return FromTags(TagMapping.Load(path), RecipeDefaults.IconFile, RecipeDefaults.DebuffColors, RecipeDefaults.TagSize, RecipeDefaults.TagCorner);
    }

    public static IReadOnlyList<BadgeOperation> FromBadges(IReadOnlyList<BadgeMappingEntry> entries, string file, BadgeStyle style)
    {
        var operations = new List<BadgeOperation>();
        foreach (BadgeMappingEntry entry in entries)
        {
            operations.Add(new BadgeOperation(file, entry.Target, entry.Badge, style));
        }

        return operations;
    }

    public static IReadOnlyList<TagOperation> FromTags(
        IReadOnlyList<TagMappingEntry> entries,
        string file,
        IReadOnlyDictionary<string, Rgb> colors,
        int size,
        IconCorner corner)
    {
        var operations = new List<TagOperation>();
        foreach (TagMappingEntry entry in entries)
        {
            if (!colors.TryGetValue(entry.Category, out Rgb color))
            {
                string known = string.Join(", ", colors.Keys);
                throw new FormatException($"Line {entry.LineNumber}: unknown category '{entry.Category}' (known: {known}).");
            }

            operations.Add(new TagOperation(file, entry.Target, color, size, corner));
        }

        return operations;
    }
}
