#:project ../src/L2Mod.Core/L2Mod.Core.csproj

// Development distribution for the CP launcher until the leader tool (Phase 2) publishes real ones.
//
// Builds a manifest from mappings\*.txt with Core's operations (plus one overlay pack with a generated PNG),
// signs it with a throwaway key whose private half is never written, and creates a FAKE client folder.
// Everything goes under <out>, which must be inside a work\ folder (gitignored, where copies of game files live)
// and must not be a real client folder.
//
//   dotnet run scripts/New-DevSource.cs -- <original Icon.utx> <out dir> [--launcher <version> <exe>] [--effects <dir>]
//   (from the repository root)
//
// --launcher offers <exe> as launcher <version> (copied to source\launcher\, hashed into the manifest), for
// checking the self-update; serve the source over http://127.0.0.1 then, folders cannot deliver updates.
// --effects takes the original LineageSkillEffect.u and LineageEffect.u from <dir> (e.g. work\original-effects):
// they are copied into the fake client's system_season and the effect packs (icons over the target, effect swaps,
// gold click arrow) are added from mappings\debuff-icons.txt and mappings\effect-swaps.txt.
//
// Result:
//   <out>\source\  manifest.json, manifest.json.sig, dev-public.pem, assets\<sha256>.png[, launcher\CpLauncher-<version>.exe]
//   <out>\client\  SysTextures\Icon.utx (copy), system_season\L2.exe (dummy), system_season\Option.ini, FAKE-CLIENT.txt
//                 [, system_season\LineageSkillEffect.u and LineageEffect.u (copies)]
//
// Start the launcher against it:
//   CpLauncher.exe --data <out>\data --client <out>\client --source <out>\source --dev-public-key <out>\source\dev-public.pem

using L2Mod.Core;
using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;
using L2Mod.Core.Publishing;
using L2Mod.Core.Recipes;
using L2Mod.Core.Signing;
using L2Mod.Core.Updates;

const string FakeMarker = "FAKE-CLIENT.txt";

var positional = new List<string>();
string? launcherVersionText = null;
string? launcherPath = null;
string? effectsDirectory = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--launcher" && i + 2 < args.Length)
    {
        launcherVersionText = args[++i];
        launcherPath = args[++i];
    }
    else if (args[i] == "--effects" && i + 1 < args.Length)
    {
        effectsDirectory = Path.GetFullPath(args[++i]);
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count != 2)
{
    Console.Error.WriteLine("usage: dotnet run scripts/New-DevSource.cs -- <original Icon.utx> <out dir> [--launcher <version> <exe>] [--effects <dir>]");
    return 1;
}

string iconPath = Path.GetFullPath(positional[0]);
string outDirectory = Path.GetFullPath(positional[1]);
LauncherRelease? launcher = null;
byte[]? launcherBytes = null;
if (launcherVersionText is not null && launcherPath is not null)
{
    if (!LauncherVersion.TryParse(launcherVersionText, out LauncherVersion? launcherVersion))
    {
        Console.Error.WriteLine($"'{launcherVersionText}' is not a launcher version like 1.0.1.");
        return 1;
    }

    launcherBytes = File.ReadAllBytes(Path.GetFullPath(launcherPath));
    launcher = new LauncherRelease(launcherVersion.ToString(), $"launcher/CpLauncher-{launcherVersion}.exe", ContentHash.Sha256Hex(launcherBytes));
}

string mappings = Path.Combine(Directory.GetCurrentDirectory(), "mappings");
if (!Directory.Exists(mappings))
{
    Console.Error.WriteLine("Run the script from the repository root (mappings\\ was not found).");
    return 1;
}

if (!IsUnderWorkFolder(outDirectory) || IsRealClient(outDirectory))
{
    Console.Error.WriteLine($"{outDirectory} is not a folder under work\\ (or it is a real client); choose e.g. work\\e2e.");
    return 1;
}

string clientDirectory = Path.Combine(outDirectory, "client");
if (Directory.Exists(clientDirectory) && !File.Exists(Path.Combine(clientDirectory, FakeMarker)))
{
    Console.Error.WriteLine($"{clientDirectory} exists and is not a fake client made by this script; refusing to touch it.");
    return 1;
}

byte[] original = File.ReadAllBytes(iconPath);
string originalHash = ContentHash.Sha256Hex(original);

byte[] markPng = PngCodec.Encode(DrawMark());
string markHash = ContentHash.Sha256Hex(markPng);
string markPath = $"assets/{markHash}.png";
var marks = new List<IPatchOperation>();
foreach (string target in new[] { "etc_i.etc_cobol_seed_i01", "etc_i.etc_reaper_i00", "etc_i.etc_dice_d_i00" })
{
    marks.Add(new OverlayOperation(ClientFiles.IconUtx, target, markPath, markHash, OverlayOperation.DefaultCorner));
}

var packs = new List<Pack>
{
    new Pack("dance-song-badges", "Znaczki tańców i pieśni", true, MappingOperations.LoadBadges(Path.Combine(mappings, "dances-songs.txt")),
        "Ikony tańców i pieśni dostają w prawym dolnym rogu miniaturę buffa o tym samym działaniu."),
    new Pack("chant-badges", "Znaczki czantów i Overlorda", true, MappingOperations.LoadBadges(Path.Combine(mappings, "chants.txt")),
        "Czanty Warcryera i buffy Overlorda dostają w prawym dolnym rogu miniaturę odpowiadającego buffa Propheta."),
    new Pack("debuff-tags", "Tagi debuffów", true, MappingOperations.LoadDebuffTags(Path.Combine(mappings, "debuff-tags.txt")),
        "Kolorowy kwadrat w lewym górnym rogu ikony debuffu: czerwony – kontrola (stun, sen, unieruchomienie, strach, cisza), fioletowy – obrażenia w czasie, pomarańczowy – osłabienie statystyk."),
    new Pack("item-marks-dev", "Oznaczenia przedmiotów (test)", false, marks,
        "Żółty romb na ikonach trzech przedmiotów – paczka tylko do testów launchera."),
};

var originals = new Dictionary<string, IReadOnlyList<string>> { [ClientFiles.IconUtx] = new[] { originalHash } };
var effectFiles = new Dictionary<string, byte[]>();
if (effectsDirectory is not null)
{
    foreach (string file in new[] { ClientFiles.LineageSkillEffect, ClientFiles.LineageEffect })
    {
        effectFiles[file] = File.ReadAllBytes(Path.Combine(effectsDirectory, file));
        originals[file] = new[] { ContentHash.Sha256Hex(effectFiles[file]) };
    }

    var icons = EffectMappings.LoadTargetIcons(Path.Combine(mappings, "debuff-icons.txt"))
        .Select(entry => new PackOption(entry.Id, entry.Label.Name, false, new IPatchOperation[] { entry.Operation }, entry.Label.Description))
        .ToList();
    var swaps = EffectMappings.LoadSwaps(Path.Combine(mappings, "effect-swaps.txt"))
        .Select(entry => new PackOption(entry.Id, entry.Label.Name, false, new IPatchOperation[] { entry.Operation }, entry.Label.Description))
        .ToList();
    packs.Add(Pack.WithOptions("target-icons", "Ikony nad celem", false, icons,
        "Ikona umiejętności unosi się nad głową celu, gdy umiejętność go trafi. To nie jest wskaźnik debuffa: pokazuje się też przy odporności (resist) i znika po 5 s."));
    packs.Add(Pack.WithOptions("skill-effect-swaps", "Zamiana efektów umiejętności", false, swaps,
        "Dwie umiejętności zamieniają się wyglądem w obie strony."));
    packs.Add(new Pack("gold-click-arrow", "Złota strzałka ruchu", false, new IPatchOperation[] { new GoldClickArrowOperation() },
        "Strzałka w miejscu kliknięcia przy chodzeniu jest złota zamiast niebieskiej."));
}

var assets = new SingleAsset(markPath, markPng);
var package = new IconPackage("Icon", L2File.FromBytes(original));
var all = packs.SelectMany(pack => pack.Operations).OfType<IIconOperation>().ToList();
OperationBuildResult build = OperationComposer.Build(package, all, assets);
Console.WriteLine($"{all.Count} icon operations build on {Path.GetFileName(iconPath)} ({build.Warnings.Count} warnings).");
foreach ((string file, byte[] bytes) in effectFiles)
{
    var operations = packs.SelectMany(pack => pack.Operations).OfType<IEffectOperation>().Where(operation => operation.File == file).ToList();
    EffectComposer.Build(file, bytes, operations, () =>
    {
        return package;
    });
    Console.WriteLine($"{operations.Count} effect operations build on {file}.");
}

var announcements = new List<Announcement>
{
    new Announcement(new DateOnly(2026, 10, 19), "Wersja deweloperska", "Manifest testowy z mapowań w repozytorium.\nPodpisany jednorazowym kluczem – nie do użytku w CP."),
    new Announcement(new DateOnly(2026, 10, 18), "Starsze ogłoszenie", "Zwykły tekst, <b>bez HTML</b>."),
};
var presets = new List<OptionPreset> { new OptionPreset("mass-pvp", "Masowe PvP", new[] { new IniValue("Video", "EffectDetail", "0") }) };
var manifestModel = new Manifest(1, DateTime.Now.ToString("yyyy.MM.dd.HHmm"), true, launcher, originals, announcements, packs, presets);
byte[] manifest = ManifestWriter.WriteChecked(manifestModel);
ManifestReadResult parsed = ManifestParser.Parse(manifest);

SigningKeyPair key = ManifestSignature.GenerateKeyPair();
byte[] signature = ManifestSignature.Sign(manifest, key.PrivateKeyPem);

string sourceDirectory = Path.Combine(outDirectory, "source");
Directory.CreateDirectory(Path.Combine(sourceDirectory, "assets"));
File.WriteAllBytes(Path.Combine(sourceDirectory, "manifest.json"), manifest);
File.WriteAllBytes(Path.Combine(sourceDirectory, "manifest.json.sig"), signature);
File.WriteAllText(Path.Combine(sourceDirectory, "dev-public.pem"), key.PublicKeyPem);
File.WriteAllBytes(Path.Combine(sourceDirectory, markPath.Replace('/', Path.DirectorySeparatorChar)), markPng);
if (launcher is not null)
{
    Directory.CreateDirectory(Path.Combine(sourceDirectory, "launcher"));
    File.WriteAllBytes(Path.Combine(sourceDirectory, launcher.Url.Replace('/', Path.DirectorySeparatorChar)), launcherBytes!);
    Console.WriteLine($"Launcher release: {launcher.Version} ({launcherBytes!.Length} bytes, SHA-256 {launcher.Sha256})");
}

Directory.CreateDirectory(Path.Combine(clientDirectory, "SysTextures"));
Directory.CreateDirectory(Path.Combine(clientDirectory, "system_season"));
File.WriteAllText(Path.Combine(clientDirectory, FakeMarker), "Fake L2Dex client for launcher tests (made by scripts/New-DevSource.cs).\r\n");
File.WriteAllBytes(Path.Combine(clientDirectory, "SysTextures", "Icon.utx"), original);
File.WriteAllText(Path.Combine(clientDirectory, "system_season", "L2.exe"), "not a real executable\r\n");
File.WriteAllText(Path.Combine(clientDirectory, "system_season", "Option.ini"), "[Video]\r\nEffectDetail=2\r\nTextureDetail=1\r\n");
foreach ((string file, byte[] bytes) in effectFiles)
{
    File.WriteAllBytes(Path.Combine(clientDirectory, ClientFiles.GetRelativePath(file)), bytes);
}

Console.WriteLine($"Source: {sourceDirectory} (manifest {parsed.Manifest.Version}, {packs.Count} packs)");
Console.WriteLine($"Fake client: {clientDirectory}");
Console.WriteLine("The private key was not saved; rerun the script to re-sign.");
return 0;

static bool IsUnderWorkFolder(string directory)
{
    for (DirectoryInfo? current = new DirectoryInfo(directory).Parent; current is not null; current = current.Parent)
    {
        if (string.Equals(current.Name, "work", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static bool IsRealClient(string directory)
{
    bool hasIcon = File.Exists(Path.Combine(directory, "SysTextures", "Icon.utx"));
    bool isFake = File.Exists(Path.Combine(directory, FakeMarker)) || File.Exists(Path.Combine(directory, "client", FakeMarker));
    return hasIcon && !isFake;
}

static RgbaImage DrawMark()
{
    // 10x10 yellow diamond with a black outline on a transparent background.
    var image = new RgbaImage(10, 10);
    for (int y = 0; y < 10; y++)
    {
        for (int x = 0; x < 10; x++)
        {
            int distance = Math.Abs((2 * x) - 9) + Math.Abs((2 * y) - 9);
            if (distance <= 7)
            {
                image.SetPixel(x, y, 255, 214, 0, 255);
            }
            else if (distance <= 10)
            {
                image.SetPixel(x, y, 0, 0, 0, 255);
            }
        }
    }

    return image;
}

sealed class SingleAsset : IAssetStore
{
    private readonly string _path;
    private readonly byte[] _bytes;

    public SingleAsset(string path, byte[] bytes)
    {
        _path = path;
        _bytes = bytes;
    }

    public byte[] ReadAsset(string path)
    {
        if (path != _path)
        {
            throw new FileNotFoundException(path);
        }

        return _bytes;
    }
}
