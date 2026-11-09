using System.Buffers.Binary;
using System.Text;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Effects;

/// <summary>
/// Swaps the effects of skill pairs in LineageSkillEffect.u, each pair both ways: the class names l2_&lt;a&gt;_skill
/// and l2_&lt;b&gt;_skill swap places in the name table (same length, so nothing moves) and so do the classes'
/// SkillID defaults, so the client finds the other skill's effect whether it looks the class up by name or by
/// SkillID. Every value replaced is first checked to be the expected original one.
/// </summary>
public static class SkillEffectSwaps
{
    public static IReadOnlyList<BodyPatch> CreatePatches(L2File file, IReadOnlyList<EffectSwapOperation> swaps)
    {
        UnrealPackage package = UnrealPackage.Parse(file.Body);
        var found = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (EffectSwapOperation swap in swaps)
        {
            found[EffectKinds.SkillClassName(swap.First)] = new List<int>();
            found[EffectKinds.SkillClassName(swap.Second)] = new List<int>();
        }

        FindNameEntries(file.Body, found);
        var patches = new List<BodyPatch>();
        foreach (EffectSwapOperation swap in swaps)
        {
            string first = EffectKinds.SkillClassName(swap.First);
            string second = EffectKinds.SkillClassName(swap.Second);
            if (found[first].Count != 1 || found[second].Count != 1 || first.Length != second.Length)
            {
                throw new InvalidDataException($"Expected exactly one name entry for {first} and {second}, of equal length.");
            }

            patches.Add(new BodyPatch(found[first][0], Encoding.Latin1.GetBytes(second)));
            patches.Add(new BodyPatch(found[second][0], Encoding.Latin1.GetBytes(first)));
            patches.Add(new BodyPatch(SkillIdOffset(package, swap.First), Int32Bytes(swap.Second)));
            patches.Add(new BodyPatch(SkillIdOffset(package, swap.Second), Int32Bytes(swap.First)));
        }

        return patches;
    }

    /// <summary>
    /// Per skill class: its SkillID default and the effect classes of its L2EffectEmitters, e.g.
    /// "l2_1235_skill (SkillID 1235) -> el_hydro_blast_ca, el_hydro_blast_ta". For build output and checks.
    /// </summary>
    public static IReadOnlyList<string> DescribeSkillClasses(byte[] fileBytes, IEnumerable<int> skillIds)
    {
        UnrealPackage package = UnrealPackage.Parse(L2File.FromBytes(fileBytes).Body);
        var lines = new List<string>();
        foreach (int id in skillIds.Distinct())
        {
            string name = EffectKinds.SkillClassName(id);
            int classRef = package.Exports.ToList().FindIndex(e => e.ClassIndex == 0 && package.GetPath(e) == name) + 1;
            int skillId = BinaryPrimitives.ReadInt32LittleEndian(package.Data.AsSpan(SkillIdOffset(package, id)));
            List<string> effects = package.Exports
                .Where(e => e.OuterIndex == classRef && package.GetClassName(e) == "L2EffectEmitter")
                .Select(e => EffectClassOf(package, e))
                .ToList();
            lines.Add($"{name} (SkillID {skillId}) -> {string.Join(", ", effects)}");
        }

        return lines;
    }

    /// <summary>The name of the class an L2EffectEmitter plays (its EffectClass property).</summary>
    public static string EffectClassOf(UnrealPackage package, ExportEntry emitter)
    {
        PropertyTag tag = PropertyTags.Read(package.Data, emitter.SerialOffset, emitter.SerialOffset + emitter.SerialSize, package.GetName)
            .First(t => t.Name == "EffectClass");
        int o = tag.ValueOffset;
        return package.GetObjectName(CompactIndex.Read(package.Data, ref o));
    }

    /// <summary>Offset of the SkillID int default inside the class named for skillId; the stored value must be skillId.</summary>
    private static int SkillIdOffset(UnrealPackage package, int skillId)
    {
        string name = EffectKinds.SkillClassName(skillId);
        int skillIdName = Enumerable.Range(0, package.Names.Count).Single(i => package.GetName(i) == "SkillID");
        byte[] nameBytes = CompactIndex.Encode(skillIdName);
        ExportEntry export = package.Exports.Single(e => package.GetClassName(e) == "Class" && package.GetPath(e) == name);
        byte[] data = package.Data;
        var offsets = new List<int>();
        int end = export.SerialOffset + export.SerialSize - nameBytes.Length - 5;
        for (int o = export.SerialOffset; o <= end; o++)
        {
            int value = o + nameBytes.Length + 1;
            bool match = data.AsSpan(o, nameBytes.Length).SequenceEqual(nameBytes)
                && data[o + nameBytes.Length] == 0x22
                && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(value)) == skillId;
            if (match)
            {
                offsets.Add(value);
            }
        }

        if (offsets.Count != 1)
        {
            throw new InvalidDataException($"Expected exactly one SkillID = {skillId} default in {name}, found {offsets.Count}.");
        }

        return offsets[0];
    }

    private static byte[] Int32Bytes(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void FindNameEntries(byte[] body, Dictionary<string, List<int>> found)
    {
        int nameCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16));
        for (int i = 0; i < nameCount; i++)
        {
            int length = CompactIndex.Read(body, ref offset);
            if (length <= 0)
            {
                throw new InvalidDataException($"Name {i} is not an ANSI name; unexpected for this package.");
            }

            string name = Encoding.Latin1.GetString(body, offset, length - 1);
            if (found.TryGetValue(name, out List<int>? offsets))
            {
                offsets.Add(offset);
            }

            offset += length + 4;
        }
    }
}
