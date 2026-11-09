#:project ../src/L2Mod.Core/L2Mod.Core.csproj

// Builds test copies of the client's system_season effect packages with L2Mod.Core's effect engine (the same code
// the launcher installs them with), for a MANUAL in-game test with scripts/Install-EffectsExperiment.ps1, and the
// probe build that finds out which effect class a skill plays.
//
//   1. Skill effect swaps from the mapping (default mappings/effect-swaps.txt), each pair both ways
//      (LineageSkillEffect.u, size-preserving, see L2Mod.Core.Effects.SkillEffectSwaps).
//   2. The click-to-move arrow turns gold (LineageEffect.u, size-preserving, ClickArrowRecolor).
//   3. Optional (--icons): icons over the target for the skills of the mapping (LineageSkillEffect.u, appended,
//      TargetIconBuilder). The pixels and the texture layout come from --icon-package, the ORIGINAL Icon.utx.
//   4. Optional (--probe, with --icons): every l2_<number>_skill class also shows its number over the target for a
//      few seconds, to see in game which effect class a skill actually plays. For a test session only, never
//      published: a skill without a class of its own needs "@<number>" in mappings/debuff-icons.txt.
//
// The script reads copies of the originals, refuses to write anywhere but inside a work\ folder, and appends the
// SHA-256 of every output to builds.txt in the out dir (Install-EffectsExperiment.ps1 recognises older builds by it).
// On the originals of 2026-10-12 (work\original-effects, work\original\Icon.utx) the build with --icons
// mappings/debuff-icons.txt gives LineageSkillEffect.u cbde648c... and LineageEffect.u ca0cada8..., byte-identical
// to this script before its engine moved to Core (commit a4cd7d4, the version tested in game).
//
//   dotnet run scripts/New-EffectsExperiment.cs -- <dir with original LineageEffect.u and LineageSkillEffect.u> <out dir>
//       [--swaps mappings/effect-swaps.txt] [--icons mappings/debuff-icons.txt --icon-package <original Icon.utx> [--probe]]

using System.Globalization;
using System.Security.Cryptography;
using L2Mod.Core;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Recipes;

var positional = new List<string>();
string swapMapping = Path.Combine("mappings", "effect-swaps.txt");
string? iconMapping = null;
string? iconPackage = null;
bool probe = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--swaps" && i + 1 < args.Length)
    {
        swapMapping = args[++i];
    }
    else if (args[i] == "--icons" && i + 1 < args.Length)
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
    Console.Error.WriteLine("usage: dotnet run scripts/New-EffectsExperiment.cs -- <original dir> <out dir under work\\> [--swaps <mapping>] [--icons <mapping> --icon-package <original Icon.utx> [--probe]]");
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
IReadOnlyList<EffectSwapOperation> swaps = EffectMappings.LoadSwaps(swapMapping).Select(entry => entry.Operation).ToList();
L2File skillEffect = L2File.Load(Path.Combine(sourceDir, ClientFiles.LineageSkillEffect));
byte[] skillEffectOut = skillEffect.BuildPatchedFile(SkillEffectSwaps.CreatePatches(skillEffect, swaps));
var shownSkills = swaps.SelectMany(swap => new[] { swap.First, swap.Second }).ToList();
if (iconMapping is not null && iconPackage is not null)
{
    var icons = new IconPackage(iconPackage);
    var builder = new TargetIconBuilder(skillEffect, skillEffectOut, icons);
    IReadOnlyList<int> classes = builder.SkillClassNumbers;
    foreach (TargetIconEntry entry in EffectMappings.LoadTargetIcons(iconMapping))
    {
        builder.AddIcon(entry.Operation);
        shownSkills.Add(entry.Operation.EffectClass);
    }

    if (probe)
    {
        var markerStyle = new SpriteStyle(16f, 75f, 0f, 6f); // above the icons, still, a few seconds
        foreach (int number in classes)
        {
            builder.AddMarker(number, NumberImages.Render(number), "skill_i.skill1056", markerStyle);
        }
    }

    skillEffectOut = builder.Build();
    foreach (string message in builder.Messages)
    {
        Console.WriteLine($"  {message}");
    }
}

string skillEffectTarget = Path.Combine(outDir, ClientFiles.LineageSkillEffect);
Write(skillEffect, skillEffectOut, skillEffectTarget);
foreach (string line in SkillEffectSwaps.DescribeSkillClasses(skillEffectOut, shownSkills))
{
    Console.WriteLine($"  {line}");
}

L2File effect = L2File.Load(Path.Combine(sourceDir, ClientFiles.LineageEffect));
string effectTarget = Path.Combine(outDir, ClientFiles.LineageEffect);
Write(effect, effect.BuildPatchedFile(ClickArrowRecolor.CreatePatches(effect)), effectTarget);
RecordBuilds(outDir, new[] { skillEffectTarget, effectTarget });
return 0;

static void Write(L2File original, byte[] output, string target)
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

    string size = $"size {original.RawBytes.Length} -> {output.Length} (+{output.Length - original.RawBytes.Length} appended)";
    if (output.Length == original.RawBytes.Length)
    {
        size = $"size {output.Length} (unchanged)";
    }

    Console.WriteLine($"{Path.GetFileName(target)}: {changed} byte(s) changed in place, {size}, SHA-256 {ContentHash.Sha256Hex(output)}");
}

// builds.txt keeps "<file name> <sha256>" of every build, so the install script can recognise an older build.
static void RecordBuilds(string outDir, IEnumerable<string> files)
{
    string list = Path.Combine(outDir, "builds.txt");
    var lines = new List<string>();
    if (File.Exists(list))
    {
        lines.AddRange(File.ReadAllLines(list));
    }

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

// Yellow digits on a dark square, 32x32, for the probe markers (5x7 font drawn 1 px wide and 2 px tall, up to 5 digits).
static class NumberImages
{
    private static readonly string[] Font =
    {
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
    };

    public static RgbaImage Render(int number)
    {
        const int Size = 32;
        string digits = number.ToString(CultureInfo.InvariantCulture);
        byte[] pixels = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++)
        {
            pixels[(i * 4) + 0] = 24;
            pixels[(i * 4) + 1] = 24;
            pixels[(i * 4) + 2] = 24;
            pixels[(i * 4) + 3] = 255;
        }

        int width = (digits.Length * 6) - 1;
        int left = (Size - width) / 2;
        const int Top = (Size - 14) / 2;
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
                        int p = (((Top + (row * 2) + dy) * Size) + left + (d * 6) + col) * 4;
                        pixels[p + 0] = 255;
                        pixels[p + 1] = 220;
                        pixels[p + 2] = 0;
                    }
                }
            }
        }

        return new RgbaImage(Size, Size, pixels);
    }
}
