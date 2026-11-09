#:project ../src/L2Mod.Core/L2Mod.Core.csproj

// Experiment (2026-11-08): cosmetic effect changes in system_season packages, for a MANUAL in-game test only
// (the launcher does not install these; L2Updater checks both files on every start).
//
//   1. Skill effect swaps in LineageSkillEffect.u, each pair both ways: Hydro Blast (1235) <-> Aqua Swirl (1175)
//      and Prominence (1230) <-> Blaze (1220). The class names l2_<a>_skill and l2_<b>_skill swap places in the
//      name table (same length, so nothing moves) and so do the classes' SkillID defaults, so the client finds the
//      other skill's effect whether it looks the class up by name or by SkillID.
//   2. The click-to-move arrow (GstarFX_arrow_click / GstarFX_arrow_loop and their _simple variants in
//      LineageEffect.u) turns gold: blue tints become gold, the flat green mesh material is replaced by a neutral
//      grey one (Aura.aura001_1) and the blue glow sprite by a golden one (Particles2.fx_m_t_3037). Only these four
//      effect classes change; the shared textures stay untouched.
//   3. Optional (--icons): debuff icons above the target. For each skill in the mapping file a new effect class
//      cp_icon_<id> (an Emitter with one camera-facing sprite, following the target) and its own texture (a
//      pre-flipped copy of Icon_h.skill_i.skill<id> from the original dir, laid out like the textures of
//      --icon-package, the client's Icon.utx) are added to LineageSkillEffect.u, and a new L2EffectEmitter for it is
//      appended to the effect class the game plays for the skill (its own l2_<id>_skill unless the mapping names
//      another one; a missing class is created, which the game ignored for Noblesse and Slow). The icon starts above
//      the head, floats up and fades out. It plays on every cast that reaches the target, so it also shows on a
//      resisted debuff and stays for a fixed time.
//   4. Optional (--probe, with --icons): every l2_<number>_skill class also shows its number above the target for a
//      few seconds, to see in game which effect class a skill actually plays. For a test session only.
//
// Changes 1 and 2 are size-preserving and in place. Change 3 appends: new and changed objects and new copies of the
// name/import/export tables go after the end of the file and the header points at them; the old tables stay as
// dead bytes and every existing object keeps its offset (L2Dex's own LineageSkillEffect.u was edited the same way).
// The script reads copies of the originals, checks that each value it replaces is the expected original one,
// refuses to write anywhere but inside a work\ folder, and appends the SHA-256 of every output to builds.txt in the
// out dir (Install-EffectsExperiment.ps1 recognises older builds by it).
//
//   dotnet run scripts/New-EffectsExperiment.cs -- <dir with original LineageEffect.u and LineageSkillEffect.u> <out dir>
//       [--icons mappings/debuff-icons.txt --icon-package <Icon.utx> [--probe]]

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

var positional = new List<string>();
string? iconMapping = null;
string? iconPackage = null;
bool probe = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--icons" && i + 1 < args.Length)
    {
        iconMapping = args[++i];
    }
    else if (args[i] == "--icon-package" && i + 1 < args.Length)
    {
        iconPackage = args[++i];
    }
    else if (args[i] == "--probe")
    {
        probe = true;
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count != 2 || (iconMapping is null) != (iconPackage is null) || (probe && iconMapping is null))
{
    Console.Error.WriteLine("usage: dotnet run scripts/New-EffectsExperiment.cs -- <original dir> <out dir under work\\> [--icons <mapping> --icon-package <Icon.utx> [--probe]]");
    return 1;
}

string sourceDir = Path.GetFullPath(positional[0]);
string outDir = Path.GetFullPath(positional[1]);
bool outInsideWork = outDir.Split(Path.DirectorySeparatorChar).Contains("work", StringComparer.OrdinalIgnoreCase);
if (!outInsideWork || string.Equals(sourceDir.TrimEnd('\\'), outDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The out dir must be inside a work\\ folder and differ from the original dir.");
    return 1;
}

Directory.CreateDirectory(outDir);
(int A, int B)[] swaps = [(1235, 1175), (1230, 1220)];
L2File skillEffect = L2File.Load(Path.Combine(sourceDir, "LineageSkillEffect.u"));
byte[] skillEffectOut = Experiment.SwapSkillEffects(skillEffect, swaps);
List<int> iconSkills = [];
if (iconMapping is not null && iconPackage is not null)
{
    (skillEffectOut, iconSkills) = DebuffIcons.Append(
        skillEffect, skillEffectOut, DebuffIcons.ReadMapping(iconMapping), iconPackage, Path.Combine(sourceDir, "Icon_h.utx"), probe);
}

string skillEffectTarget = Path.Combine(outDir, "LineageSkillEffect.u");
Experiment.Write(skillEffect, skillEffectOut, skillEffectTarget);
Experiment.PrintSkillEffects(skillEffectOut, swaps.SelectMany(p => new[] { p.A, p.B }).Concat(iconSkills));

string effectTarget = Path.Combine(outDir, "LineageEffect.u");
Experiment.GoldClickArrow(Path.Combine(sourceDir, "LineageEffect.u"), effectTarget);
Experiment.RecordBuilds(outDir, [skillEffectTarget, effectTarget]);
return 0;

sealed record Tag(string Name, int Type, string StructName, int ValueOffset, int Size);

static class Experiment
{
    private static readonly string[] ArrowClasses =
    [
        "GstarFX_arrow_click",
        "GstarFX_arrow_click_simple",
        "GstarFX_arrow_loop",
        "GstarFX_arrow_loop_simple",
    ];

    // Colours as stored (B, G, R); alpha stays. Blue arrow tint and the light-blue star colours -> gold.
    private static readonly Dictionary<int, byte[]> ColourMap = new()
    {
        [Bgr(0xFF, 0x80, 0x00)] = [0x28, 0xB9, 0xFF],
        [Bgr(0xFF, 0xBF, 0x80)] = [0x5A, 0xC8, 0xFF],
        [Bgr(0xFF, 0xD5, 0xAA)] = [0x8C, 0xE1, 0xFF],
    };

    public static byte[] SwapSkillEffects(L2File file, IReadOnlyList<(int A, int B)> pairs)
    {
        UnrealPackage package = UnrealPackage.Parse(file.Body);
        var found = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach ((int a, int b) in pairs)
        {
            found[ClassName(a)] = [];
            found[ClassName(b)] = [];
        }

        FindNameEntries(file.Body, found);
        var patches = new List<BodyPatch>();
        foreach ((int a, int b) in pairs)
        {
            string nameA = ClassName(a);
            string nameB = ClassName(b);
            if (found[nameA].Count != 1 || found[nameB].Count != 1 || nameA.Length != nameB.Length)
            {
                throw new InvalidDataException($"Expected exactly one name entry for {nameA} and {nameB}, of equal length.");
            }

            patches.Add(new BodyPatch(found[nameA][0], Encoding.Latin1.GetBytes(nameB)));
            patches.Add(new BodyPatch(found[nameB][0], Encoding.Latin1.GetBytes(nameA)));
            patches.Add(new BodyPatch(SkillIdOffset(package, a), Int32Bytes(b)));
            patches.Add(new BodyPatch(SkillIdOffset(package, b), Int32Bytes(a)));
        }

        return file.BuildPatchedFile(patches);
    }

    // Lists, per skill class, the effect classes of all its L2EffectEmitters (cp_icon_* ones with their texture).
    public static void PrintSkillEffects(byte[] fileBytes, IEnumerable<int> skillIds)
    {
        UnrealPackage check = UnrealPackage.Parse(L2File.FromBytes(fileBytes).Body);
        foreach (int id in skillIds.Distinct())
        {
            string name = ClassName(id);
            int classIndex = check.Exports.ToList().FindIndex(e => e.ClassIndex == 0 && check.GetPath(e) == name) + 1;
            int skillId = BinaryPrimitives.ReadInt32LittleEndian(check.Data.AsSpan(SkillIdOffset(check, id)));
            List<string> effects = check.Exports
                .Where(e => e.OuterIndex == classIndex && check.GetClassName(e) == "L2EffectEmitter")
                .Select(e => EffectClassOf(check, e))
                .ToList();
            Console.WriteLine($"  {name} (SkillID {skillId}) -> {string.Join(", ", effects)}");
        }
    }

    public static string ClassName(int skillId)
    {
        return $"l2_{skillId}_skill";
    }

    // Offset of the SkillID int default inside the class named for skillId; the stored value must be skillId.
    private static int SkillIdOffset(UnrealPackage package, int skillId)
    {
        string name = ClassName(skillId);
        int skillIdName = Enumerable.Range(0, package.Names.Count).Single(i => package.GetName(i) == "SkillID");
        byte[] nameBytes = EncodeCompact(skillIdName);
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
            int length = ReadCompact(body, ref offset);
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

    public static void GoldClickArrow(string source, string target)
    {
        L2File file = L2File.Load(source);
        UnrealPackage package = UnrealPackage.Parse(file.Body);
        int blueGlow = ImportReference(package, "Texture", "Particles", "FX_M_T4111");
        int goldGlow = ImportReference(package, "Texture", "Particles2", "fx_m_t_3037");
        int greenFlat = ImportReference(package, "Texture", "Particles", "fx_m_t8254");
        int greyAura = ImportReference(package, "Texture", "Aura", "aura001_1");
        byte[] data = package.Data;
        var patches = new List<BodyPatch>();
        var touched = new HashSet<string>();

        foreach (ExportEntry export in package.Exports)
        {
            string path = package.GetPath(export);
            string className = package.GetClassName(export);
            int dot = path.IndexOf('.');
            if (dot < 0 || !ArrowClasses.Contains(path[..dot]) || !className.EndsWith("Emitter", StringComparison.Ordinal))
            {
                continue;
            }

            int end = export.SerialOffset + export.SerialSize;
            foreach (Tag tag in ReadTags(package, export.SerialOffset, end, out _))
            {
                int before = patches.Count;
                if (tag.Name == "Texture" && tag.Type == 5)
                {
                    ReplaceReference(data, tag.ValueOffset, tag.Size, blueGlow, goldGlow, patches);
                }
                else if (tag.Name == "CustomMaterials" && tag.Type == 9)
                {
                    int o = tag.ValueOffset;
                    int count = ReadCompact(data, ref o);
                    for (int i = 0; i < count; i++)
                    {
                        int start = o;
                        ReadCompact(data, ref o);
                        ReplaceReference(data, start, o - start, greenFlat, greyAura, patches);
                    }
                }
                else if (tag.Name == "ColorScale" && tag.Type == 9)
                {
                    RecolourColorScale(package, tag, patches);
                }
                else if (tag.Name == "ColorMultiplierRange" && tag.Type == 10)
                {
                    WarmColorMultiplier(package, tag, patches);
                }

                if (patches.Count > before)
                {
                    touched.Add($"{path} {tag.Name}");
                }
            }
        }

        if (patches.Count == 0)
        {
            throw new InvalidOperationException($"Nothing to patch for {Path.GetFileName(target)}.");
        }

        Write(file, file.BuildPatchedFile(patches), target);
        Console.WriteLine($"  {touched.Count} emitter properties recoloured in {string.Join(", ", ArrowClasses)}");
    }

    private static void RecolourColorScale(UnrealPackage package, Tag tag, List<BodyPatch> patches)
    {
        byte[] data = package.Data;
        int o = tag.ValueOffset;
        int end = tag.ValueOffset + tag.Size;
        int count = ReadCompact(data, ref o);
        for (int i = 0; i < count; i++)
        {
            foreach (Tag element in ReadTags(package, o, end, out o))
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

    // A blue-leaning multiplier (blue clearly above red and green) becomes warm: R 1.0, G 0.8, B 0.4.
    private static void WarmColorMultiplier(UnrealPackage package, Tag tag, List<BodyPatch> patches)
    {
        var axes = new Dictionary<string, Dictionary<string, int>>();
        foreach (Tag axis in ReadTags(package, tag.ValueOffset, tag.ValueOffset + tag.Size, out _))
        {
            axes[axis.Name] = ReadTags(package, axis.ValueOffset, axis.ValueOffset + axis.Size, out _)
                .Where(t => t.Type == 4)
                .ToDictionary(t => t.Name, t => t.ValueOffset);
        }

        string[] names = ["X", "Y", "Z"];
        if (!names.All(n => axes.TryGetValue(n, out var range) && range.ContainsKey("Min") && range.ContainsKey("Max")))
        {
            return;
        }

        float Mean(string axis)
        {
            return (ReadFloat(package.Data, axes[axis]["Min"]) + ReadFloat(package.Data, axes[axis]["Max"])) / 2;
        }

        if (Mean("Z") < Mean("X") + 0.2f || Mean("Z") < Mean("Y") + 0.2f)
        {
            return;
        }

        float[] warm = [1.0f, 0.8f, 0.4f];
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
        if (ReadCompact(data, ref o) != from || o - offset != size)
        {
            return;
        }

        byte[] encoded = EncodeCompact(to);
        if (encoded.Length != size)
        {
            throw new InvalidDataException($"Replacement reference {to} does not fit in {size} byte(s).");
        }

        patches.Add(new BodyPatch(offset, encoded));
    }

    public static List<Tag> ReadTags(UnrealPackage package, int start, int end, out int next)
    {
        byte[] data = package.Data;
        var tags = new List<Tag>();
        int o = start;
        while (o < end)
        {
            string name = package.GetName(ReadCompact(data, ref o));
            if (name == "None")
            {
                break;
            }

            byte info = data[o++];
            int type = info & 0x0F;
            string structName = type == 10 ? package.GetName(ReadCompact(data, ref o)) : string.Empty;
            int size;
            switch ((info >> 4) & 7)
            {
                case 0: size = 1; break;
                case 1: size = 2; break;
                case 2: size = 4; break;
                case 3: size = 12; break;
                case 4: size = 16; break;
                case 5: size = data[o]; o += 1; break;
                case 6: size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)); o += 2; break;
                default: size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4; break;
            }

            if (type == 3)
            {
                size = 0;
            }
            else if ((info & 0x80) != 0)
            {
                if (data[o] >= 0x80)
                {
                    throw new InvalidDataException($"Array index >= 128 for {name}; not handled.");
                }

                o++;
            }

            tags.Add(new Tag(name, type, structName, o, size));
            o += size;
        }

        if (o > end)
        {
            throw new InvalidDataException("Property stream ran past its object.");
        }

        next = o;
        return tags;
    }

    public static string EffectClassOf(UnrealPackage package, ExportEntry emitter)
    {
        Tag tag = ReadTags(package, emitter.SerialOffset, emitter.SerialOffset + emitter.SerialSize, out _)
            .First(t => t.Name == "EffectClass");
        int o = tag.ValueOffset;
        return package.GetObjectName(ReadCompact(package.Data, ref o));
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

    public static void Write(L2File original, byte[] output, string target)
    {
        File.WriteAllBytes(target, output);
        int common = Math.Min(output.Length, original.RawBytes.Length);
        int changed = 0;
        for (int i = 0; i < common; i++)
        {
            if (output[i] != original.RawBytes[i])
            {
                changed++;
            }
        }

        string size = output.Length == original.RawBytes.Length
            ? $"size {output.Length} (unchanged)"
            : $"size {original.RawBytes.Length} -> {output.Length} (+{output.Length - original.RawBytes.Length} appended)";
        string hash = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
        Console.WriteLine($"{Path.GetFileName(target)}: {changed} byte(s) changed in place, {size}, SHA-256 {hash}");
    }

    // builds.txt keeps "<file name> <sha256>" of every build, so the install script can recognise an older build.
    public static void RecordBuilds(string outDir, IEnumerable<string> files)
    {
        string list = Path.Combine(outDir, "builds.txt");
        var lines = File.Exists(list) ? File.ReadAllLines(list).ToList() : [];
        foreach (string file in files)
        {
            string line = $"{Path.GetFileName(file)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()}";
            if (!lines.Contains(line))
            {
                lines.Add(line);
            }
        }

        File.WriteAllLines(list, lines);
    }

    private static int Bgr(byte blue, byte green, byte red)
    {
        return (blue << 16) | (green << 8) | red;
    }

    private static float ReadFloat(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
    }

    public static int ReadCompact(byte[] data, ref int offset)
    {
        byte first = data[offset++];
        int value = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6;
            byte next;
            do
            {
                next = data[offset++];
                value |= (next & 0x7F) << shift;
                shift += 7;
            }
            while ((next & 0x80) != 0 && shift < 32);
        }

        return (first & 0x80) != 0 ? -value : value;
    }

    public static byte[] EncodeCompact(int value)
    {
        int magnitude = Math.Abs(value);
        byte sign = value < 0 ? (byte)0x80 : (byte)0;
        if (magnitude < 0x40)
        {
            return [(byte)(sign | magnitude)];
        }

        var bytes = new List<byte> { (byte)(sign | 0x40 | (magnitude & 0x3F)) };
        magnitude >>= 6;
        while (magnitude >= 0x80)
        {
            bytes.Add((byte)(0x80 | (magnitude & 0x7F)));
            magnitude >>= 7;
        }

        bytes.Add((byte)magnitude);
        return [.. bytes];
    }
}

// EffectClassId: the skill effect class the game plays for the skill (l2_<id>_skill), when it is not the skill's own.
sealed record IconRule(int SkillId, float Seconds, int EffectClassId);

// Change 3: an icon effect class per debuff, appended to LineageSkillEffect.u. The class layout (UStruct/UState/UClass
// fields, then the tagged defaults) and the property values follow LineageEffect.u's effect classes, e.g.
// a_u011_death_penalty (an Emitter with one SpriteEmitter) and L2Dex's dex_zone_* icon sprites.
// With --probe every l2_<number>_skill class also shows its own number above the target, to find out in game which
// effect class a skill without its own class actually plays.
static class DebuffIcons
{
    private const float DefaultSeconds = 5f;
    private const float IconHalfSize = 10f;    // sprite half-size in world units
    private const float IconHeight = 40f;      // icon centre at spawn, above the top of the target's collision cylinder
    private const float RiseSpeed = 8f;        // the icon floats up this many units per second
    private const float ProbeHalfSize = 16f;
    private const float ProbeHeight = 75f;     // above the icons
    private const float ProbeSeconds = 6f;
    private const string ProbeLayoutIcon = "skill_i.skill1056"; // any 32x32 one-mip icon: layout of the number textures
    private const byte AttachTrail = 9;        // L2EffectEmitter.AttachMethod AM_Trail: follows the actor
    private const byte CoordinatesRelative = 1; // PTCS_Relative: the particle moves with the emitter
    private const byte DrawAlphaBlend = 1;     // PTDS_AlphaBlend
    private const uint ClassExportFlags = 0x000E0004;
    private const uint TextBufferFlags = 0x00240000;
    private const uint SubobjectFlags = 0x00060000;
    private const uint EmitterClassFlags = 0x212;
    private const ulong EmitterProbeMask = 0x0080000000000040;
    private const uint EmitterScriptCrc = 0x92E2C039; // Engine.Emitter as recorded by LineageEffect.u's classes
    private const uint SkillEffectClassFlags = 0x12;
    private const uint SkillEffectScriptCrc = 0x5DE7EE79; // Engine.L2SkillEffect as recorded by the l2_*_skill classes
    private const string PackageName = "LineageSkillEffect";

    // Format per line: <skill id> [seconds] [@<effect class id>]  # comment
    public static List<IconRule> ReadMapping(string path)
    {
        var rules = new List<IconRule>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 3 || !TryParseId(parts[0], out int id))
            {
                throw new InvalidDataException($"{path}: bad line '{raw}' (expected '<skill id> [seconds] [@<effect class id>]').");
            }

            float seconds = DefaultSeconds;
            int effectClassId = id;
            foreach (string part in parts.Skip(1))
            {
                bool ok = part.StartsWith('@')
                    ? TryParseId(part[1..], out effectClassId)
                    : float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 1 && seconds <= 120;
                if (!ok)
                {
                    throw new InvalidDataException($"{path}: bad value '{part}' in '{raw}' (seconds 1-120, or @<effect class id>).");
                }
            }

            if (rules.Any(r => r.SkillId == id || r.EffectClassId == effectClassId))
            {
                throw new InvalidDataException($"{path}: skill {id} or effect class {effectClassId} is listed twice.");
            }

            rules.Add(new IconRule(id, seconds, effectClassId));
        }

        return rules;
    }

    private static bool TryParseId(string text, out int id)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private sealed class Context(PackageEditor editor, UnrealPackage icons, string iconPackageName)
    {
        public PackageEditor Editor { get; } = editor;

        public UnrealPackage Icons { get; } = icons;

        public string IconPackageName { get; } = iconPackageName;

        public int ObjectClass { get; set; }

        public int TextBufferClass { get; set; }

        public int EmitterClass { get; set; }

        public int SpriteEmitterClass { get; set; }

        public int EffectEmitterClass { get; set; }

        public int SkillEffectClass { get; set; }

        public int TextureClass { get; set; }
    }

    // iconPackagePath: the client's Icon.utx (same package version as LineageSkillEffect.u), the layout template of
    // each icon texture. iconPixelsPath: an unpatched copy of the icons (Icon_h.utx, same pictures without our corner
    // badges/tags), where the pixels come from. Both are only read.
    public static (byte[] Output, List<int> Added) Append(
        L2File original, byte[] fileBytes, IReadOnlyList<IconRule> rules, string iconPackagePath, string iconPixelsPath, bool probe)
    {
        if (original.Encryption != L2Encryption.None)
        {
            throw new NotSupportedException("Icons can only be appended to an unencrypted package (L2Dex's LineageSkillEffect.u is one).");
        }

        UnrealPackage icons = UnrealPackage.Parse(L2File.Load(iconPackagePath).Body);
        UnrealPackage pixels = UnrealPackage.Parse(L2File.Load(iconPixelsPath).Body);
        if (icons.FileVersion != UnrealPackage.Parse(fileBytes).FileVersion)
        {
            throw new InvalidDataException($"{Path.GetFileName(iconPackagePath)} is not the same package version as LineageSkillEffect.u.");
        }

        var editor = new PackageEditor(fileBytes);
        int corePackage = editor.Import("Core", "Package", 0, "Core");
        int enginePackage = editor.Import("Core", "Package", 0, "Engine");
        var ctx = new Context(editor, icons, Path.GetFileNameWithoutExtension(iconPackagePath))
        {
            ObjectClass = editor.Import("Core", "Class", corePackage, "Object"),
            TextBufferClass = editor.Import("Core", "Class", corePackage, "TextBuffer"),
            EmitterClass = editor.Import("Core", "Class", enginePackage, "Emitter"),
            SpriteEmitterClass = editor.Import("Core", "Class", enginePackage, "SpriteEmitter"),
            EffectEmitterClass = editor.Import("Core", "Class", enginePackage, "L2EffectEmitter"),
            SkillEffectClass = editor.Import("Core", "Class", enginePackage, "L2SkillEffect"),
            TextureClass = editor.Import("Core", "Class", enginePackage, "Texture"),
        };

        // Classes as they were before anything was added: the probe numbers go on these.
        List<(int Ref, int Number)> probeTargets = editor.TopLevelClasses()
            .Select(c => (c.Ref, Number: SkillClassNumber(c.Name)))
            .Where(c => c.Number > 0)
            .ToList();

        var added = new List<IconRule>();
        foreach (IconRule rule in rules)
        {
            string iconPath = $"skill_i.skill{rule.SkillId:D4}";
            ExportEntry? layout = FindTexture(icons, iconPath);
            ExportEntry? source = FindTexture(pixels, iconPath);
            if (layout is null || source is null)
            {
                Console.WriteLine($"  skipped {rule.SkillId}: no {iconPath} in {Path.GetFileName(iconPackagePath)} or {Path.GetFileName(iconPixelsPath)}");
                continue;
            }

            TextureData sourceTexture = TextureReader.Read(pixels, source);
            MipLevel mip = sourceTexture.Mips[0];
            byte[] flipped = IconTextures.FlipDxt1(pixels.Data.AsSpan(mip.DataOffset, mip.DataSize), sourceTexture.Width, sourceTexture.Height);
            int iconClass = AddSpriteClass(ctx, $"cp_icon_{rule.SkillId}", $"cp_icon_tex_{rule.SkillId}", layout, iconPath, flipped, IconHalfSize, IconHeight, RiseSpeed, rule.Seconds);

            string skillClassName = Experiment.ClassName(rule.EffectClassId);
            int skillClass = editor.FindClass(skillClassName);
            if (skillClass == 0)
            {
                // A skill without an effect class of its own gets a new one that only shows the icon. In game this
                // did not work for Noblesse Blessing and Slow (the client picks their effect elsewhere).
                skillClass = editor.AddExport(0, ctx.SkillEffectClass, 0, skillClassName, ClassExportFlags);
                int skillText = editor.AddExport(ctx.TextBufferClass, 0, skillClass, "ScriptText", TextBufferFlags, EmptyTextBuffer(editor));
                int newEffect = editor.AddExport(ctx.EffectEmitterClass, 0, skillClass, "L2EffectEmitter0", SubobjectFlags, EffectEmitterData(editor, iconClass));
                editor.SetData(skillClass, ClassData(
                    editor, skillClass, ctx.SkillEffectClass, skillText, ctx.ObjectClass, 0, SkillEffectClassFlags, SkillEffectScriptCrc, ["Object"],
                    tags =>
                    {
                        tags.Str("Desc", $"cp debuff icon {rule.SkillId}");
                        tags.Int("SkillID", rule.EffectClassId);
                        tags.ObjectArray("ShotAction", [newEffect]);
                    }));
                Console.WriteLine($"  {skillClassName}: created (the package had no effect class for this skill)");
            }
            else
            {
                string action = AttachToSkillClass(ctx, skillClass, iconClass);
                if (rule.EffectClassId != rule.SkillId || action != "ShotAction")
                {
                    Console.WriteLine($"  {rule.SkillId}: icon added to {skillClassName}.{action}");
                }
            }

            added.Add(rule);
        }

        if (added.Count == 0)
        {
            throw new InvalidOperationException("No debuff icon could be added.");
        }

        var probes = new List<int>();
        if (probe)
        {
            ExportEntry layout = FindTexture(icons, ProbeLayoutIcon) ?? throw new InvalidDataException($"No {ProbeLayoutIcon} for the number textures.");
            foreach ((int skillClass, int number) in probeTargets)
            {
                byte[] flipped = IconTextures.FlipDxt1(Dxt1Codec.Encode(NumberImages.Render(number)), 32, 32);
                int probeClass = AddSpriteClass(ctx, $"cp_probe_{number}", $"cp_probe_tex_{number}", layout, ProbeLayoutIcon, flipped, ProbeHalfSize, ProbeHeight, 0f, ProbeSeconds);
                AttachToSkillClass(ctx, skillClass, probeClass);
                probes.Add(number);
            }

            Console.WriteLine($"  probe: number markers on {probes.Count} effect classes");
        }

        byte[] output = editor.Build();
        Verify(fileBytes, output, added, probes, pixels);
        return (output, added.Select(r => r.EffectClassId).ToList());
    }

    private static ExportEntry? FindTexture(UnrealPackage package, string path)
    {
        return package.Exports.FirstOrDefault(e => package.GetClassName(e) == "Texture" && package.GetPath(e) == path);
    }

    // l2_<number>_skill -> number; 0 for any other name.
    private static int SkillClassNumber(string name)
    {
        if (!name.StartsWith("l2_", StringComparison.Ordinal) || !name.EndsWith("_skill", StringComparison.Ordinal))
        {
            return 0;
        }

        string digits = name[3..^6];
        return digits.Length is > 0 and <= 5 && digits.All(char.IsAsciiDigit) && TryParseId(digits, out int number) ? number : 0;
    }

    // An Emitter class with one camera-facing sprite showing a 32x32 texture (stored flipped, see IconTextures).
    private static int AddSpriteClass(
        Context ctx,
        string className,
        string textureName,
        ExportEntry layout,
        string layoutPath,
        byte[] flippedDxt1,
        float halfSize,
        float height,
        float riseSpeed,
        float seconds)
    {
        PackageEditor editor = ctx.Editor;
        if (editor.FindClass(className) != 0)
        {
            throw new InvalidDataException($"{className} already exists: build from the original package.");
        }

        byte[] textureData = IconTextures.Build(
            editor, ctx.Icons, layout, $"{ctx.IconPackageName}.{layoutPath}", flippedDxt1, $"{PackageName}.{textureName}", out int skipAt, out int skipValue);
        int texture = editor.AddExport(ctx.TextureClass, 0, 0, textureName, layout.ObjectFlags, textureData);
        editor.AddOffsetFixup(texture, skipAt, skipValue);
        int spriteClass = editor.AddExport(0, ctx.EmitterClass, 0, className, ClassExportFlags);
        int text = editor.AddExport(ctx.TextBufferClass, 0, spriteClass, "ScriptText", TextBufferFlags, EmptyTextBuffer(editor));
        int sprite = editor.AddExport(
            ctx.SpriteEmitterClass, 0, spriteClass, "SpriteEmitter0", SubobjectFlags, SpriteDefaults(editor, texture, halfSize, height, riseSpeed, seconds));
        editor.SetData(spriteClass, ClassData(
            editor, spriteClass, ctx.EmitterClass, text, ctx.ObjectClass, EmitterProbeMask, EmitterClassFlags, EmitterScriptCrc, [],
            tags =>
            {
                tags.ObjectArray("Emitters", [sprite]);
                tags.Bool("bLightChanged", true);
                tags.Bool("bNoDelete", false);
            }));
        return spriteClass;
    }

    // Appends an L2EffectEmitter for effectClass where the skill class already plays its target effects: ShotAction,
    // or CastingAction for classes that have no ShotAction (e.g. l2_6673_skill, Noblesse's), else a new ShotAction.
    private static string AttachToSkillClass(Context ctx, int skillClass, int effectClass)
    {
        PackageEditor editor = ctx.Editor;
        int effect = editor.AddExport(
            ctx.EffectEmitterClass, 0, skillClass, NextEmitterName(editor, skillClass), SubobjectFlags, EffectEmitterData(editor, effectClass));
        byte[] data = editor.GetData(skillClass);
        List<RawTag> tags = ClassLayout.ScanTags(data, ClassLayout.DefaultsOffset(data), data.Length, editor.NameAt);
        string action = tags.Any(t => t.Name == "ShotAction" && t.Type == 9) || !tags.Any(t => t.Name == "CastingAction" && t.Type == 9)
            ? "ShotAction"
            : "CastingAction";
        editor.SetData(skillClass, AppendToAction(editor, skillClass, action, effect));
        return action;
    }

    private static byte[] EmptyTextBuffer(PackageEditor editor)
    {
        var w = new ByteWriter();
        w.Compact(editor.Name("None"));
        w.Int32(0);
        w.Int32(0);
        w.Compact(0);
        return w.ToArray();
    }

    // A script-less UClass as this package version stores it, followed by its tagged defaults.
    private static byte[] ClassData(
        PackageEditor editor,
        int classRef,
        int superClass,
        int scriptText,
        int within,
        ulong probeMask,
        uint classFlags,
        uint superScriptCrc,
        IReadOnlyList<string> hideCategories,
        Action<TagWriter> defaults)
    {
        var w = new ByteWriter();
        w.Compact(superClass);                       // UField.SuperField
        w.Compact(0);                                // UField.Next
        w.Compact(scriptText);                       // UStruct.ScriptText
        w.Compact(0);                                // UStruct.Children
        w.Compact(editor.Name(editor.ExportName(classRef))); // UStruct.FriendlyName
        w.Compact(0);                                // UStruct.CppText
        w.Int32(-1);                                 // Line
        w.Int32(-1);                                 // TextPos
        w.Int32(0);                                  // ScriptSize
        w.UInt64(probeMask);                         // UState.ProbeMask
        w.UInt64(ulong.MaxValue);                    // UState.IgnoreMask
        w.UInt16(0xFFFF);                            // UState.LabelTableOffset
        w.UInt32(0);                                 // UState.StateFlags
        w.UInt32(classFlags);                        // UClass.ClassFlags
        w.Bytes(new byte[16]);                       // UClass.ClassGuid
        w.Compact(2);                                // Dependencies: the class itself and its super class
        w.Compact(classRef);
        w.Int32(1);
        w.UInt32(0);
        w.Compact(superClass);
        w.Int32(1);
        w.UInt32(superScriptCrc);
        w.Compact(3);                                // PackageImports
        w.Compact(editor.Name("LineageSkillEffect"));
        w.Compact(editor.Name("Engine"));
        w.Compact(editor.Name("Core"));
        w.Compact(within);                           // ClassWithin
        w.Compact(editor.Name("System"));            // ClassConfigName
        w.Compact(hideCategories.Count);             // HideCategories
        foreach (string category in hideCategories)
        {
            w.Compact(editor.Name(category));
        }

        var tags = new TagWriter(editor, w);
        defaults(tags);
        tags.None();
        return w.ToArray();
    }

    private static byte[] SpriteDefaults(PackageEditor editor, int texture, float halfSize, float height, float riseSpeed, float seconds)
    {
        var w = new ByteWriter();
        var tags = new TagWriter(editor, w);
        tags.Float("FadeOutStartTime", Math.Max(seconds - 1f, seconds / 2f));
        tags.Bool("FadeOut", true);
        tags.Float("FadeInEndTime", 0.15f);
        tags.Bool("FadeIn", true);
        tags.Byte("CoordinateSystem", CoordinatesRelative);
        tags.Int("MaxParticles", 1);
        tags.Bool("RespawnDeadParticles", false);
        tags.Vector("StartLocationOffset", 0f, 0f, height);
        tags.Bool("UniformSize", true);
        tags.RangeVector("StartSizeRange", halfSize, halfSize, halfSize);
        tags.RangeVector("StartVelocityRange", 0f, 0f, riseSpeed);
        tags.Float("InitialParticlesPerSecond", 1000f);
        tags.Bool("AutomaticInitialSpawning", false);
        tags.Byte("DrawStyle", DrawAlphaBlend);
        tags.Object("Texture", texture);
        tags.Float("SecondsBeforeInactive", 0f);
        tags.Range("LifetimeRange", seconds, seconds);
        tags.None();
        return w.ToArray();
    }

    // Like Surrender's own on-target effects: on the target, following it; offset is relative to its collision
    // cylinder (Z 1 = top of the head). bAdjustLifeTime off so the engine keeps the sprite's own lifetime.
    private static byte[] EffectEmitterData(PackageEditor editor, int iconClass)
    {
        var w = new ByteWriter();
        var tags = new TagWriter(editor, w);
        tags.Byte("AttachOn", AttachTrail);
        tags.Vector("offset", 0f, 0f, 1f);
        tags.Bool("bOnMultiTarget", true);
        tags.Bool("bAdjustLifeTime", false);
        tags.Object("EffectClass", iconClass);
        tags.Object("SimpleEffectClass", iconClass);
        tags.None();
        return w.ToArray();
    }

    private static string NextEmitterName(PackageEditor editor, int skillClass)
    {
        int next = 0;
        foreach (string name in editor.ChildNames(skillClass))
        {
            if (name.StartsWith("L2EffectEmitter", StringComparison.Ordinal) && int.TryParse(name["L2EffectEmitter".Length..], out int n))
            {
                next = Math.Max(next, n + 1);
            }
        }

        return $"L2EffectEmitter{next}";
    }

    // Rewrites the skill class's defaults with the new emitter appended to the given action array (added when missing).
    private static byte[] AppendToAction(PackageEditor editor, int skillClass, string action, int effect)
    {
        byte[] data = editor.GetData(skillClass);
        int defaults = ClassLayout.DefaultsOffset(data);
        var w = new ByteWriter();
        w.Bytes(data.AsSpan(0, defaults));
        var tags = new TagWriter(editor, w);
        bool done = false;
        foreach (RawTag tag in ClassLayout.ScanTags(data, defaults, data.Length, editor.NameAt))
        {
            if (tag.Name == action && tag.Type == 9)
            {
                int o = tag.ValueOffset;
                int count = Experiment.ReadCompact(data, ref o);
                var refs = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    refs.Add(Experiment.ReadCompact(data, ref o));
                }

                refs.Add(effect);
                tags.ObjectArray(action, refs);
                done = true;
            }
            else
            {
                w.Bytes(data.AsSpan(tag.Start, tag.End - tag.Start));
            }
        }

        if (!done)
        {
            tags.ObjectArray(action, [effect]);
        }

        tags.None();
        return w.ToArray();
    }

    private static void Verify(byte[] before, byte[] after, List<IconRule> added, List<int> probes, UnrealPackage pixels)
    {
        UnrealPackage old = UnrealPackage.Parse(before);
        UnrealPackage check = UnrealPackage.Parse(after);
        var changed = new List<string>();
        for (int i = 0; i < old.Exports.Count; i++)
        {
            ExportEntry a = old.Exports[i];
            ExportEntry b = check.Exports[i];
            bool sameEntry = a.ClassIndex == b.ClassIndex && a.SuperIndex == b.SuperIndex && a.OuterIndex == b.OuterIndex
                && check.GetName(b.ObjectNameIndex) == old.GetName(a.ObjectNameIndex) && a.ObjectFlags == b.ObjectFlags;
            if (!sameEntry)
            {
                throw new InvalidDataException($"Export {i + 1} changed its table entry.");
            }

            if (a.SerialOffset != b.SerialOffset || a.SerialSize != b.SerialSize)
            {
                changed.Add(check.GetPath(b));
            }
        }

        // Every class (old, changed and new) must decode with its defaults ending exactly at the end of the object,
        // and every new texture must read back like an icon texture (the reader checks the mip skip offsets).
        int classes = 0;
        int textures = 0;
        foreach (ExportEntry e in check.Exports)
        {
            if (e.ClassIndex == 0)
            {
                byte[] data = check.Data.AsSpan(e.SerialOffset, e.SerialSize).ToArray();
                List<RawTag> tags = ClassLayout.ScanTags(data, ClassLayout.DefaultsOffset(data), data.Length, check.GetName);
                if (tags.Count == 0 || tags[^1].End + 1 != data.Length)
                {
                    throw new InvalidDataException($"{check.GetPath(e)}: defaults do not end exactly at the end of the object.");
                }

                classes++;
            }
            else if (check.GetClassName(e) == "Texture" && check.GetName(e.ObjectNameIndex).StartsWith("cp_", StringComparison.Ordinal))
            {
                if (TextureReader.Read(check, e).Mips.Count != 1)
                {
                    throw new InvalidDataException($"{check.GetPath(e)}: expected one mip.");
                }

                textures++;
            }
        }

        foreach (IconRule rule in added)
        {
            // The icon texture shows the source icon upside down, pixel for pixel.
            TextureData ours = TextureReader.Read(check, check.Exports.Single(e => check.GetPath(e) == $"cp_icon_tex_{rule.SkillId}"));
            ExportEntry sourceExport = FindTexture(pixels, $"skill_i.skill{rule.SkillId:D4}")!;
            TextureData source = TextureReader.Read(pixels, sourceExport);
            RgbaImage flipped = Dxt1Codec.Decode(check.Data.AsSpan(ours.Mips[0].DataOffset, ours.Mips[0].DataSize), ours.Width, ours.Height);
            RgbaImage icon = Dxt1Codec.Decode(pixels.Data.AsSpan(source.Mips[0].DataOffset, source.Mips[0].DataSize), source.Width, source.Height);
            int rowBytes = icon.Width * 4;
            for (int y = 0; y < icon.Height; y++)
            {
                if (!flipped.Pixels.AsSpan(y * rowBytes, rowBytes).SequenceEqual(icon.Pixels.AsSpan((icon.Height - 1 - y) * rowBytes, rowBytes)))
                {
                    throw new InvalidDataException($"{ours.Path} is not the flipped {pixels.GetPath(sourceExport)} icon.");
                }
            }
        }

        Console.WriteLine($"  checked: {classes} classes decode exactly, {textures} new textures read back, {added.Count} icons match their source flipped");
        int shown = Math.Min(changed.Count, 12);
        Console.WriteLine($"  existing objects rewritten (appended): {changed.Count} ({string.Join(", ", changed.Take(shown))}{(changed.Count > shown ? ", ..." : string.Empty)})");
    }
}

// Yellow digits on a dark square, 32x32, for the probe markers (5x7 font drawn 1 px wide and 2 px tall, up to 5 digits).
static class NumberImages
{
    private static readonly string[] Font =
    [
        "01110100011001110101110011000101110",
        "00100011000010000100001000010001110",
        "01110100010000100010001000100011111",
        "11111000100010000010000011000101110",
        "00010001100101010010111110001000010",
        "11111100001111000001000011000101110",
        "00110010001000011110100011000101110",
        "11111000010001000100010000100001000",
        "01110100011000101110100011000101110",
        "01110100011000101111000010001001100",
    ];

    public static RgbaImage Render(int number)
    {
        const int size = 32;
        string digits = number.ToString(CultureInfo.InvariantCulture);
        byte[] pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            pixels[(i * 4) + 0] = 24;
            pixels[(i * 4) + 1] = 24;
            pixels[(i * 4) + 2] = 24;
            pixels[(i * 4) + 3] = 255;
        }

        int width = (digits.Length * 6) - 1;
        int left = (size - width) / 2;
        const int top = (size - 14) / 2;
        for (int d = 0; d < digits.Length; d++)
        {
            string glyph = Font[digits[d] - '0'];
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if (glyph[(row * 5) + col] != '1')
                    {
                        continue;
                    }

                    for (int dy = 0; dy < 2; dy++)
                    {
                        int p = ((((top + (row * 2) + dy) * size) + left + (d * 6) + col) * 4);
                        pixels[p + 0] = 255;
                        pixels[p + 1] = 220;
                        pixels[p + 2] = 0;
                    }
                }
            }
        }

        return new RgbaImage(size, size, pixels);
    }
}

sealed record RawTag(string Name, int Type, int Start, int ValueOffset, int Size, int End);

// Texture objects for the over-head icons, laid out like the client's Icon.utx textures of the same package version:
// tagged properties, then Lineage 2's material block (identical in every icon except for the object's own full name),
// then the mips (one here). Sprites draw textures upside down (in-game test 2026-11-08: the icon's top-left corner
// showed bottom-left), so the DXT1 data is stored flipped vertically: block rows in reverse order and the four pixel
// rows inside each block reversed, which is lossless.
static class IconTextures
{
    // Lengths of the material block around the name string, as in every Icon.utx texture checked (2026-11-08).
    private const int MaterialBlockBeforeName = 93;
    private const int MaterialBlockAfterName = 142;

    public static byte[] Build(
        PackageEditor editor,
        UnrealPackage layoutPackage,
        ExportEntry layout,
        string layoutFullName,
        byte[] flippedDxt1,
        string fullName,
        out int skipAt,
        out int skipValue)
    {
        byte[] d = layoutPackage.Data;
        int start = layout.SerialOffset;
        int end = start + layout.SerialSize;
        TextureData layoutTexture = TextureReader.Read(layoutPackage, layout);
        if (flippedDxt1.Length != layoutTexture.Mips[0].DataSize)
        {
            throw new InvalidDataException($"{layoutTexture.Path}: the new pixels do not fit its size {layoutTexture.Width}x{layoutTexture.Height}.");
        }

        var w = new ByteWriter();
        var tags = new TagWriter(editor, w);
        List<RawTag> properties = ClassLayout.ScanTags(d, start, end, layoutPackage.GetName);
        int propertiesEnd = start;
        foreach (RawTag p in properties)
        {
            if (p.Type is not (1 or 2 or 3))
            {
                throw new InvalidDataException($"{layoutTexture.Path}: unexpected property {p.Name} (type {p.Type}).");
            }

            // Byte/int/bool tags hold no name or object references after the name, so the rest is copied as is.
            int afterName = p.Start;
            Experiment.ReadCompact(d, ref afterName);
            w.Compact(editor.Name(p.Name));
            w.Bytes(d.AsSpan(afterName, p.End - afterName));
            propertiesEnd = p.End;
        }

        Experiment.ReadCompact(d, ref propertiesEnd); // None
        tags.None();

        MipLevel mip = layoutTexture.Mips[0];
        int mipTable = mip.DataOffset - Experiment.EncodeCompact(mip.DataSize).Length - 4 - Experiment.EncodeCompact(layoutTexture.Mips.Count).Length;
        byte[] oldName = Encoding.Latin1.GetBytes(layoutFullName + "\0");
        int nameAt = d.AsSpan(propertiesEnd, mipTable - propertiesEnd).IndexOf(oldName) + propertiesEnd;
        int nameLength = Experiment.EncodeCompact(oldName.Length).Length;
        int before = nameAt - nameLength - propertiesEnd;
        int after = mipTable - (nameAt + oldName.Length);
        int check = nameAt - nameLength;
        if (nameAt < propertiesEnd || before != MaterialBlockBeforeName || after != MaterialBlockAfterName || Experiment.ReadCompact(d, ref check) != oldName.Length)
        {
            throw new InvalidDataException($"{layoutTexture.Path}: material block is not laid out as expected (name at {nameAt - start}, {before} bytes before, {after} after).");
        }

        w.Bytes(d.AsSpan(propertiesEnd, before));
        w.Compact(fullName.Length + 1);
        w.Bytes(Encoding.Latin1.GetBytes(fullName));
        w.Byte(0);
        w.Bytes(d.AsSpan(nameAt + oldName.Length, after));

        w.Compact(1);                       // one mip
        skipAt = w.Length;
        w.Int32(0);                         // absolute offset of the end of the mip data, set when the package is built
        w.Compact(mip.DataSize);
        w.Bytes(flippedDxt1);
        skipValue = w.Length;
        w.Bytes(d.AsSpan(mip.DataOffset + mip.DataSize, 10)); // USize, VSize, UBits, VBits of mip 0
        return w.ToArray();
    }

    public static byte[] FlipDxt1(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width % 4 != 0 || height % 4 != 0)
        {
            throw new InvalidDataException($"DXT1 flip needs whole 4x4 blocks, not {width}x{height}.");
        }

        int blocksX = width / 4;
        int blocksY = height / 4;
        if (data.Length != blocksX * blocksY * 8)
        {
            throw new InvalidDataException("DXT1 data size does not match the texture size.");
        }

        byte[] result = new byte[data.Length];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int source = (((blocksY - 1 - by) * blocksX) + bx) * 8;
                int target = ((by * blocksX) + bx) * 8;
                data.Slice(source, 4).CopyTo(result.AsSpan(target, 4)); // the two endpoint colours
                for (int row = 0; row < 4; row++)
                {
                    result[target + 4 + row] = data[source + 7 - row];
                }
            }
        }

        return result;
    }
}

static class ClassLayout
{
    // Offset of the tagged defaults in a UClass object of this package version (133/40), checked against all 4936
    // classes of LineageSkillEffect.u and the effect classes of LineageEffect.u. Only script-less classes.
    public static int DefaultsOffset(byte[] data)
    {
        int o = 0;
        for (int i = 0; i < 6; i++)
        {
            Experiment.ReadCompact(data, ref o); // SuperField, Next, ScriptText, Children, FriendlyName, CppText
        }

        o += 8; // Line, TextPos
        int scriptSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
        if (scriptSize != 0)
        {
            throw new NotSupportedException("Classes with script bytecode are not handled.");
        }

        o += 4 + 22 + 4 + 16; // ScriptSize, UState fields, ClassFlags, ClassGuid
        int dependencies = Experiment.ReadCompact(data, ref o);
        for (int i = 0; i < dependencies; i++)
        {
            Experiment.ReadCompact(data, ref o);
            o += 8;
        }

        int packageImports = Experiment.ReadCompact(data, ref o);
        for (int i = 0; i < packageImports; i++)
        {
            Experiment.ReadCompact(data, ref o);
        }

        Experiment.ReadCompact(data, ref o); // ClassWithin
        Experiment.ReadCompact(data, ref o); // ClassConfigName
        int hideCategories = Experiment.ReadCompact(data, ref o);
        for (int i = 0; i < hideCategories; i++)
        {
            Experiment.ReadCompact(data, ref o);
        }

        return o;
    }

    // Tags up to (not including) None; RawTag.End is where the next tag starts.
    public static List<RawTag> ScanTags(byte[] data, int start, int end, Func<int, string> nameAt)
    {
        var tags = new List<RawTag>();
        int o = start;
        while (o < end)
        {
            int tagStart = o;
            string name = nameAt(Experiment.ReadCompact(data, ref o));
            if (name == "None")
            {
                return tags;
            }

            byte info = data[o++];
            int type = info & 0x0F;
            if (type == 10)
            {
                Experiment.ReadCompact(data, ref o);
            }

            int size;
            switch ((info >> 4) & 7)
            {
                case 0: size = 1; break;
                case 1: size = 2; break;
                case 2: size = 4; break;
                case 3: size = 12; break;
                case 4: size = 16; break;
                case 5: size = data[o]; o += 1; break;
                case 6: size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)); o += 2; break;
                default: size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4; break;
            }

            if (type == 3)
            {
                size = 0;
            }
            else if ((info & 0x80) != 0)
            {
                o++;
            }

            tags.Add(new RawTag(name, type, tagStart, o, size, o + size));
            o += size;
        }

        throw new InvalidDataException("Tagged properties without a None terminator.");
    }
}

sealed class ByteWriter
{
    private readonly MemoryStream stream = new();

    public int Length => (int)stream.Length;

    public void Compact(int value)
    {
        stream.Write(Experiment.EncodeCompact(value));
    }

    public void Byte(byte value)
    {
        stream.WriteByte(value);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        stream.Write(value);
    }

    public void Int32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        stream.Write(b);
    }

    public void UInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        stream.Write(b);
    }

    public void UInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        stream.Write(b);
    }

    public void UInt64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        stream.Write(b);
    }

    public void Single(float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        stream.Write(b);
    }

    public byte[] ToArray()
    {
        return stream.ToArray();
    }
}

// UE2 tagged properties as this package version writes them: bools carry their value in bit 7 with a size byte of 0,
// structs name their struct after the info byte, Range/RangeVector are nested tagged structs, Vector is 12 raw bytes.
sealed class TagWriter(PackageEditor editor, ByteWriter w)
{
    public void Bool(string name, bool value)
    {
        w.Compact(editor.Name(name));
        w.Byte((byte)(0x53 | (value ? 0x80 : 0)));
        w.Byte(0);
    }

    public void Byte(string name, byte value)
    {
        Tag(name, 1, null, [value]);
    }

    public void Int(string name, int value)
    {
        var v = new ByteWriter();
        v.Int32(value);
        Tag(name, 2, null, v.ToArray());
    }

    public void Float(string name, float value)
    {
        var v = new ByteWriter();
        v.Single(value);
        Tag(name, 4, null, v.ToArray());
    }

    public void Object(string name, int reference)
    {
        Tag(name, 5, null, Experiment.EncodeCompact(reference));
    }

    public void ObjectArray(string name, IReadOnlyList<int> references)
    {
        var v = new ByteWriter();
        v.Compact(references.Count);
        foreach (int reference in references)
        {
            v.Compact(reference);
        }

        Tag(name, 9, null, v.ToArray());
    }

    public void Vector(string name, float x, float y, float z)
    {
        var v = new ByteWriter();
        v.Single(x);
        v.Single(y);
        v.Single(z);
        Tag(name, 10, "Vector", v.ToArray());
    }

    public void Range(string name, float min, float max)
    {
        Tag(name, 10, "Range", RangeBytes(min, max));
    }

    // Each axis as a fixed value (Min = Max).
    public void RangeVector(string name, float x, float y, float z)
    {
        var v = new ByteWriter();
        var inner = new TagWriter(editor, v);
        inner.Tag("X", 10, "Range", RangeBytes(x, x));
        inner.Tag("Y", 10, "Range", RangeBytes(y, y));
        inner.Tag("Z", 10, "Range", RangeBytes(z, z));
        inner.None();
        Tag(name, 10, "RangeVector", v.ToArray());
    }

    public void Str(string name, string value)
    {
        var v = new ByteWriter();
        v.Compact(value.Length + 1);
        v.Bytes(Encoding.Latin1.GetBytes(value));
        v.Byte(0);
        Tag(name, 13, null, v.ToArray());
    }

    public void None()
    {
        w.Compact(editor.Name("None"));
    }

    private byte[] RangeBytes(float min, float max)
    {
        var v = new ByteWriter();
        var inner = new TagWriter(editor, v);
        inner.Float("Min", min);
        inner.Float("Max", max);
        inner.None();
        return v.ToArray();
    }

    private void Tag(string name, int type, string? structName, byte[] value)
    {
        int sizeCode = value.Length switch
        {
            1 => 0,
            2 => 1,
            4 => 2,
            12 => 3,
            16 => 4,
            <= 255 => 5,
            <= 65535 => 6,
            _ => 7,
        };
        w.Compact(editor.Name(name));
        w.Byte((byte)(type | (sizeCode << 4)));
        if (structName is not null)
        {
            w.Compact(editor.Name(structName));
        }

        if (sizeCode == 5)
        {
            w.Byte((byte)value.Length);
        }
        else if (sizeCode == 6)
        {
            w.UInt16((ushort)value.Length);
        }
        else if (sizeCode == 7)
        {
            w.Int32(value.Length);
        }

        w.Bytes(value);
    }
}

// Append-only package editing: the original bytes stay as they are; new and changed objects, then new copies of the
// name, import and export tables are written after them and the header is pointed at the new tables.
sealed class PackageEditor
{
    private readonly byte[] original;
    private readonly List<string> names;
    private readonly Dictionary<string, int> nameLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<byte[]> addedNameEntries = [];
    private readonly List<ImportEntry> imports;
    private readonly int originalImportCount;
    private readonly List<ExportRow> exports;
    private readonly int nameTableStart;
    private readonly int nameTableEnd;
    private readonly int importTableStart;
    private readonly int importTableEnd;
    private readonly uint nameFlags;
    private Dictionary<string, int>? topLevelClasses;
    private Dictionary<int, List<string>>? children;

    public PackageEditor(byte[] package)
    {
        original = package;
        UnrealPackage parsed = UnrealPackage.Parse(package);
        names = parsed.Names.ToList();
        for (int i = 0; i < names.Count; i++)
        {
            nameLookup.TryAdd(names[i], i);
        }

        imports = parsed.Imports.ToList();
        originalImportCount = imports.Count;
        exports = parsed.Exports.Select(e => new ExportRow(e)).ToList();

        nameTableStart = HeaderInt(16);
        int o = nameTableStart;
        var flagCounts = new Dictionary<uint, int>();
        for (int i = 0; i < names.Count; i++)
        {
            int length = Experiment.ReadCompact(package, ref o);
            o += length < 0 ? -length * 2 : length;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(o));
            flagCounts[flags] = flagCounts.GetValueOrDefault(flags) + 1;
            o += 4;
        }

        nameTableEnd = o;
        nameFlags = flagCounts.MaxBy(kv => kv.Value).Key;

        importTableStart = HeaderInt(32);
        o = importTableStart;
        for (int i = 0; i < imports.Count; i++)
        {
            Experiment.ReadCompact(package, ref o);
            Experiment.ReadCompact(package, ref o);
            o += 4;
            Experiment.ReadCompact(package, ref o);
        }

        importTableEnd = o;

        // The export table is rewritten from the parsed rows; make sure that reproduces the original bytes exactly.
        byte[] table = SerializeExports();
        if (!package.AsSpan(HeaderInt(24), table.Length).SequenceEqual(table))
        {
            throw new InvalidDataException("Re-serialising the export table does not reproduce the original; format not understood.");
        }
    }

    public int Name(string name)
    {
        if (nameLookup.TryGetValue(name, out int index))
        {
            return index;
        }

        var w = new ByteWriter();
        w.Compact(name.Length + 1);
        w.Bytes(Encoding.Latin1.GetBytes(name));
        w.Byte(0);
        w.UInt32(nameFlags);
        addedNameEntries.Add(w.ToArray());
        names.Add(name);
        nameLookup[name] = names.Count - 1;
        return names.Count - 1;
    }

    public string NameAt(int index)
    {
        return names[index];
    }

    public string ExportName(int exportRef)
    {
        return names[exports[exportRef - 1].Name];
    }

    public int Import(string classPackage, string className, int outer, string objectName)
    {
        for (int i = 0; i < imports.Count; i++)
        {
            ImportEntry e = imports[i];
            bool match = string.Equals(names[e.ClassPackageIndex], classPackage, StringComparison.OrdinalIgnoreCase)
                && string.Equals(names[e.ClassNameIndex], className, StringComparison.OrdinalIgnoreCase)
                && e.OuterIndex == outer
                && string.Equals(names[e.ObjectNameIndex], objectName, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return -(i + 1);
            }
        }

        imports.Add(new ImportEntry(Name(classPackage), Name(className), outer, Name(objectName)));
        return -imports.Count;
    }

    public int FindClass(string name)
    {
        IndexExports();
        return topLevelClasses!.TryGetValue(name, out int exportRef) ? exportRef : 0;
    }

    public IEnumerable<(int Ref, string Name)> TopLevelClasses()
    {
        IndexExports();
        return topLevelClasses!.Select(kv => (kv.Value, kv.Key)).OrderBy(c => c.Value).ToList();
    }

    public IEnumerable<string> ChildNames(int outer)
    {
        IndexExports();
        return children!.TryGetValue(outer, out List<string>? list) ? list : [];
    }

    // Lookups by class name and by outer, built once and kept up to date by AddExport (thousands of objects get added).
    private void IndexExports()
    {
        if (topLevelClasses is not null)
        {
            return;
        }

        topLevelClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        children = [];
        for (int i = 0; i < exports.Count; i++)
        {
            Index(i + 1);
        }
    }

    private void Index(int exportRef)
    {
        ExportRow e = exports[exportRef - 1];
        if (e.Class == 0 && e.Outer == 0)
        {
            topLevelClasses!.TryAdd(names[e.Name], exportRef);
        }

        if (!children!.TryGetValue(e.Outer, out List<string>? list))
        {
            list = [];
            children[e.Outer] = list;
        }

        list.Add(names[e.Name]);
    }

    public byte[] GetData(int exportRef)
    {
        ExportRow row = exports[exportRef - 1];
        return row.Data ?? original.AsSpan(row.Offset, row.Size).ToArray();
    }

    public int AddExport(int classRef, int superRef, int outerRef, string name, uint flags, byte[]? data = null)
    {
        exports.Add(new ExportRow(classRef, superRef, outerRef, Name(name), flags) { Data = data ?? [] });
        if (topLevelClasses is not null)
        {
            Index(exports.Count);
        }

        return exports.Count;
    }

    public void SetData(int exportRef, byte[] data)
    {
        exports[exportRef - 1].Data = data;
    }

    // At build time the int at `position` in the object's data becomes the object's file offset + `relative`
    // (texture mips store the absolute offset of the end of their data).
    public void AddOffsetFixup(int exportRef, int position, int relative)
    {
        exports[exportRef - 1].Fixups.Add((position, relative));
    }

    public byte[] Build()
    {
        var output = new MemoryStream();
        output.Write(original);
        foreach (ExportRow row in exports.Where(e => e.Data is not null))
        {
            row.Offset = (int)output.Length;
            row.Size = row.Data!.Length;
            byte[] data = (byte[])row.Data.Clone();
            foreach ((int position, int relative) in row.Fixups)
            {
                SetHeaderInt(data, position, row.Offset + relative);
            }

            output.Write(data);
        }

        int nameOffset = (int)output.Length;
        output.Write(original.AsSpan(nameTableStart, nameTableEnd - nameTableStart));
        foreach (byte[] entry in addedNameEntries)
        {
            output.Write(entry);
        }

        int importOffset = (int)output.Length;
        output.Write(original.AsSpan(importTableStart, importTableEnd - importTableStart));
        var w = new ByteWriter();
        foreach (ImportEntry e in imports.Skip(originalImportCount))
        {
            w.Compact(e.ClassPackageIndex);
            w.Compact(e.ClassNameIndex);
            w.Int32(e.OuterIndex);
            w.Compact(e.ObjectNameIndex);
        }

        output.Write(w.ToArray());
        int exportOffset = (int)output.Length;
        output.Write(SerializeExports());

        byte[] result = output.ToArray();
        SetHeaderInt(result, 12, names.Count);
        SetHeaderInt(result, 16, nameOffset);
        SetHeaderInt(result, 20, exports.Count);
        SetHeaderInt(result, 24, exportOffset);
        SetHeaderInt(result, 28, imports.Count);
        SetHeaderInt(result, 32, importOffset);
        int generations = HeaderInt(52);
        if (generations > 0)
        {
            int last = 56 + ((generations - 1) * 8);
            SetHeaderInt(result, last, exports.Count);
            SetHeaderInt(result, last + 4, names.Count);
        }

        return result;
    }

    private byte[] SerializeExports()
    {
        var w = new ByteWriter();
        foreach (ExportRow e in exports)
        {
            w.Compact(e.Class);
            w.Compact(e.Super);
            w.Int32(e.Outer);
            w.Compact(e.Name);
            w.UInt32(e.Flags);
            w.Compact(e.Size);
            if (e.Size > 0)
            {
                w.Compact(e.Offset);
            }
        }

        return w.ToArray();
    }

    private int HeaderInt(int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset));
    }

    private static void SetHeaderInt(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }

    private sealed class ExportRow
    {
        public ExportRow(ExportEntry e)
        {
            Class = e.ClassIndex;
            Super = e.SuperIndex;
            Outer = e.OuterIndex;
            Name = e.ObjectNameIndex;
            Flags = e.ObjectFlags;
            Size = e.SerialSize;
            Offset = e.SerialOffset;
        }

        public ExportRow(int classRef, int superRef, int outerRef, int name, uint flags)
        {
            Class = classRef;
            Super = superRef;
            Outer = outerRef;
            Name = name;
            Flags = flags;
        }

        public int Class { get; }

        public int Super { get; }

        public int Outer { get; }

        public int Name { get; }

        public uint Flags { get; }

        public int Size { get; set; }

        public int Offset { get; set; }

        public byte[]? Data { get; set; }

        public List<(int Position, int Relative)> Fixups { get; } = [];
    }
}
