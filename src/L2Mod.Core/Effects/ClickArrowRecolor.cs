using System.Buffers.Binary;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Effects;

/// <summary>
/// Turns the click-to-move arrow (GstarFX_arrow_click / GstarFX_arrow_loop and their _simple variants in
/// LineageEffect.u) gold: blue tints become gold, the flat green mesh material is replaced by a neutral grey one
/// (Aura.aura001_1) and the blue glow sprite by a golden one (Particles2.fx_m_t_3037). Only these four effect classes
/// change; the shared textures stay untouched. Size-preserving.
/// </summary>
public static class ClickArrowRecolor
{
    private static readonly string[] ArrowClasses =
    {
        "GstarFX_arrow_click",
        "GstarFX_arrow_click_simple",
        "GstarFX_arrow_loop",
        "GstarFX_arrow_loop_simple",
    };

    /// <summary>Colours as stored (B, G, R); alpha stays. The blue arrow tint and the light-blue star colours -> gold.</summary>
    private static readonly Dictionary<int, byte[]> ColourMap = new Dictionary<int, byte[]>
    {
        [Bgr(0xFF, 0x80, 0x00)] = new byte[] { 0x28, 0xB9, 0xFF },
        [Bgr(0xFF, 0xBF, 0x80)] = new byte[] { 0x5A, 0xC8, 0xFF },
        [Bgr(0xFF, 0xD5, 0xAA)] = new byte[] { 0x8C, 0xE1, 0xFF },
    };

    public static IReadOnlyList<BodyPatch> CreatePatches(L2File file)
    {
        UnrealPackage package = UnrealPackage.Parse(file.Body);
        int blueGlow = ImportReference(package, "Texture", "Particles", "FX_M_T4111");
        int goldGlow = ImportReference(package, "Texture", "Particles2", "fx_m_t_3037");
        int greenFlat = ImportReference(package, "Texture", "Particles", "fx_m_t8254");
        int greyAura = ImportReference(package, "Texture", "Aura", "aura001_1");
        byte[] data = package.Data;
        var patches = new List<BodyPatch>();

        foreach (ExportEntry export in package.Exports)
        {
            string path = package.GetPath(export);
            string className = package.GetClassName(export);
            int dot = path.IndexOf('.');
            if (dot < 0 || !ArrowClasses.Contains(path.Substring(0, dot)) || !className.EndsWith("Emitter", StringComparison.Ordinal))
            {
                continue;
            }

            int end = export.SerialOffset + export.SerialSize;
            foreach (PropertyTag tag in PropertyTags.Read(data, export.SerialOffset, end, package.GetName))
            {
                if (tag.Name == "Texture" && tag.Type == PropertyTypes.Object)
                {
                    ReplaceReference(data, tag.ValueOffset, tag.Size, blueGlow, goldGlow, patches);
                }
                else if (tag.Name == "CustomMaterials" && tag.Type == PropertyTypes.Array)
                {
                    int o = tag.ValueOffset;
                    int count = CompactIndex.Read(data, ref o);
                    for (int i = 0; i < count; i++)
                    {
                        int start = o;
                        CompactIndex.Read(data, ref o);
                        ReplaceReference(data, start, o - start, greenFlat, greyAura, patches);
                    }
                }
                else if (tag.Name == "ColorScale" && tag.Type == PropertyTypes.Array)
                {
                    RecolourColorScale(package, tag, patches);
                }
                else if (tag.Name == "ColorMultiplierRange" && tag.Type == PropertyTypes.Struct)
                {
                    WarmColorMultiplier(package, tag, patches);
                }
            }
        }

        if (patches.Count == 0)
        {
            throw new InvalidDataException("The click-to-move arrow has nothing to recolour in this LineageEffect.u.");
        }

        return patches;
    }

    private static void RecolourColorScale(UnrealPackage package, PropertyTag tag, List<BodyPatch> patches)
    {
        byte[] data = package.Data;
        int o = tag.ValueOffset;
        int end = tag.ValueOffset + tag.Size;
        int count = CompactIndex.Read(data, ref o);
        for (int i = 0; i < count; i++)
        {
            foreach (PropertyTag element in PropertyTags.Read(data, o, end, package.GetName, out o))
            {
                if (element.Name != "Color" || element.Size != 4)
                {
                    continue;
                }

                int key = Bgr(data[element.ValueOffset], data[element.ValueOffset + 1], data[element.ValueOffset + 2]);
                if (ColourMap.TryGetValue(key, out byte[]? gold))
                {
                    patches.Add(new BodyPatch(element.ValueOffset, gold));
                }
            }
        }
    }

    /// <summary>A blue-leaning multiplier (blue clearly above red and green) becomes warm: R 1.0, G 0.8, B 0.4.</summary>
    private static void WarmColorMultiplier(UnrealPackage package, PropertyTag tag, List<BodyPatch> patches)
    {
        byte[] data = package.Data;
        var axes = new Dictionary<string, Dictionary<string, int>>();
        foreach (PropertyTag axis in PropertyTags.Read(data, tag.ValueOffset, tag.ValueOffset + tag.Size, package.GetName))
        {
            axes[axis.Name] = PropertyTags.Read(data, axis.ValueOffset, axis.ValueOffset + axis.Size, package.GetName)
                .Where(t => t.Type == PropertyTypes.Float)
                .ToDictionary(t => t.Name, t => t.ValueOffset);
        }

        string[] names = { "X", "Y", "Z" };
        foreach (string name in names)
        {
            if (!axes.TryGetValue(name, out Dictionary<string, int>? range) || !range.ContainsKey("Min") || !range.ContainsKey("Max"))
            {
                return;
            }
        }

        float Mean(string axis)
        {
            return (ReadFloat(data, axes[axis]["Min"]) + ReadFloat(data, axes[axis]["Max"])) / 2;
        }

        if (Mean("Z") < Mean("X") + 0.2f || Mean("Z") < Mean("Y") + 0.2f)
        {
            return;
        }

        float[] warm = { 1.0f, 0.8f, 0.4f };
        for (int i = 0; i < names.Length; i++)
        {
            byte[] value = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(value, warm[i]);
            patches.Add(new BodyPatch(axes[names[i]]["Min"], value));
            patches.Add(new BodyPatch(axes[names[i]]["Max"], value));
        }
    }

    private static void ReplaceReference(byte[] data, int offset, int size, int from, int to, List<BodyPatch> patches)
    {
        int o = offset;
        if (CompactIndex.Read(data, ref o) != from || o - offset != size)
        {
            return;
        }

        byte[] encoded = CompactIndex.Encode(to);
        if (encoded.Length != size)
        {
            throw new InvalidDataException($"Replacement reference {to} does not fit in {size} byte(s).");
        }

        patches.Add(new BodyPatch(offset, encoded));
    }

    private static int ImportReference(UnrealPackage package, string className, string group, string name)
    {
        for (int i = 0; i < package.Imports.Count; i++)
        {
            ImportEntry import = package.Imports[i];
            bool match = package.GetName(import.ClassNameIndex) == className
                && string.Equals(package.GetName(import.ObjectNameIndex), name, StringComparison.OrdinalIgnoreCase)
                && import.OuterIndex != 0
                && string.Equals(package.GetObjectName(import.OuterIndex), group, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return -(i + 1);
            }
        }

        throw new InvalidDataException($"Import {className} {group}.{name} not found.");
    }

    private static int Bgr(byte blue, byte green, byte red)
    {
        return (blue << 16) | (green << 8) | red;
    }

    private static float ReadFloat(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
    }
}
