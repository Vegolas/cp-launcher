#:project ../src/L2Mod.Core/L2Mod.Core.csproj

// Development distribution for the CP launcher until the leader tool (Phase 2) publishes real ones.
//
// Builds a manifest from mappings\*.txt with Core's operations (plus one overlay pack with a generated PNG),
// signs it with a throwaway key whose private half is never written, and creates a FAKE client folder.
// Everything goes under <out>, which must be inside a work\ folder (gitignored, where copies of game files live)
// and must not be a real client folder.
//
//   dotnet run scripts/New-DevSource.cs -- <original Icon.utx> <out dir> [--launcher <version> <exe>]   (from the repository root)
//
// --launcher offers <exe> as launcher <version> (copied to source\launcher\, hashed into the manifest), for
// checking the self-update; serve the source over http://127.0.0.1 then, folders cannot deliver updates.
//
// Result:
//   <out>\source\  manifest.json, manifest.json.sig, dev-public.pem, assets\<sha256>.png[, launcher\CpLauncher-<version>.exe]
//   <out>\client\  SysTextures\Icon.utx (copy), system_season\L2.exe (dummy), system_season\Option.ini, FAKE-CLIENT.txt
//
// Start the launcher against it:
//   CpLauncher.exe --data <out>\data --client <out>\client --source <out>\source --dev-public-key <out>\source\dev-public.pem

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;
using L2Mod.Core.Recipes;
using L2Mod.Core.Signing;
using L2Mod.Core.Updates;

const string FakeMarker = "FAKE-CLIENT.txt";

bool withLauncher = args.Length == 5 && args[2] == "--launcher";
if (args.Length != 2 && !withLauncher)
{
    Console.Error.WriteLine("usage: dotnet run scripts/New-DevSource.cs -- <original Icon.utx> <out dir> [--launcher <version> <exe>]");
    return 1;
}

string iconPath = Path.GetFullPath(args[0]);
string outDirectory = Path.GetFullPath(args[1]);
LauncherRelease? launcher = null;
byte[]? launcherBytes = null;
if (withLauncher)
{
    if (!LauncherVersion.TryParse(args[3], out LauncherVersion? launcherVersion))
    {
        Console.Error.WriteLine($"'{args[3]}' is not a launcher version like 1.0.1.");
        return 1;
    }

    launcherBytes = File.ReadAllBytes(Path.GetFullPath(args[4]));
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
var marks = new List<IIconOperation>();
foreach (string target in new[] { "etc_i.etc_cobol_seed_i01", "etc_i.etc_reaper_i00", "etc_i.etc_dice_d_i00" })
{
    marks.Add(new OverlayOperation(ClientFiles.IconUtx, target, markPath, markHash, OverlayOperation.DefaultCorner));
}

var packs = new List<(string Id, string Name, string Description, bool Default, IReadOnlyList<IIconOperation> Operations)>
{
    ("dance-song-badges", "Znaczki tańców i pieśni", "Ikony tańców i pieśni dostają w prawym dolnym rogu miniaturę buffa o tym samym działaniu.",
        true, MappingOperations.LoadBadges(Path.Combine(mappings, "dances-songs.txt"))),
    ("chant-badges", "Znaczki czantów i Overlorda", "Czanty Warcryera i buffy Overlorda dostają w prawym dolnym rogu miniaturę odpowiadającego buffa Propheta.",
        true, MappingOperations.LoadBadges(Path.Combine(mappings, "chants.txt"))),
    ("debuff-tags", "Tagi debuffów", "Kolorowy kwadrat w lewym górnym rogu ikony debuffu: czerwony – kontrola (stun, sen, unieruchomienie, strach, cisza), fioletowy – obrażenia w czasie, pomarańczowy – osłabienie statystyk.",
        true, MappingOperations.LoadDebuffTags(Path.Combine(mappings, "debuff-tags.txt"))),
    ("item-marks-dev", "Oznaczenia przedmiotów (test)", "Żółty romb na ikonach trzech przedmiotów – paczka tylko do testów launchera.",
        false, marks),
};

var assets = new SingleAsset(markPath, markPng);
var package = new IconPackage("Icon", L2File.FromBytes(original));
var all = packs.SelectMany(pack => pack.Operations).ToList();
OperationBuildResult build = OperationComposer.Build(package, all, assets);
Console.WriteLine($"{all.Count} operations build on {Path.GetFileName(iconPath)} ({build.Warnings.Count} warnings).");

byte[] manifest = WriteManifest(originalHash, packs, launcher);
ManifestReadResult parsed = ManifestParser.Parse(manifest);
if (parsed.Messages.Count > 0 || parsed.Manifest.Packs.Count != packs.Count)
{
    Console.Error.WriteLine("The dev manifest does not round-trip: " + string.Join("; ", parsed.Messages));
    return 1;
}

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

static byte[] WriteManifest(string originalHash, IReadOnlyList<(string Id, string Name, string Description, bool Default, IReadOnlyList<IIconOperation> Operations)> packs, LauncherRelease? launcher)
{
    using var stream = new MemoryStream();
    var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    using (var writer = new Utf8JsonWriter(stream, options))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);
        writer.WriteString("version", DateTime.Now.ToString("yyyy.MM.dd.HHmm"));
        writer.WriteBoolean("enabled", true);
        if (launcher is not null)
        {
            writer.WriteStartObject("launcher");
            writer.WriteString("version", launcher.Version);
            writer.WriteString("url", launcher.Url);
            writer.WriteString("sha256", launcher.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteStartObject("supportedOriginals");
        writer.WriteStartArray(ClientFiles.IconUtx);
        writer.WriteStringValue(originalHash);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartArray("announcements");
        WriteAnnouncement(writer, "2026-10-19", "Wersja deweloperska", "Manifest testowy z mapowań w repozytorium.\nPodpisany jednorazowym kluczem – nie do użytku w CP.");
        WriteAnnouncement(writer, "2026-10-18", "Starsze ogłoszenie", "Zwykły tekst, <b>bez HTML</b>.");
        writer.WriteEndArray();
        writer.WriteStartArray("packs");
        foreach ((string id, string name, string description, bool isDefault, IReadOnlyList<IIconOperation> operations) in packs)
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WriteBoolean("default", isDefault);
            writer.WriteStartArray("operations");
            foreach (IIconOperation operation in operations)
            {
                WriteOperation(writer, operation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("optionPresets");
        writer.WriteStartObject();
        writer.WriteString("id", "mass-pvp");
        writer.WriteString("name", "Masowe PvP");
        writer.WriteStartObject("values");
        writer.WriteStartObject("Video");
        writer.WriteString("EffectDetail", "0");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    return stream.ToArray();
}

static void WriteAnnouncement(Utf8JsonWriter writer, string date, string title, string text)
{
    writer.WriteStartObject();
    writer.WriteString("date", date);
    writer.WriteString("title", title);
    writer.WriteString("text", text);
    writer.WriteEndObject();
}

static void WriteOperation(Utf8JsonWriter writer, IIconOperation operation)
{
    writer.WriteStartObject();
    writer.WriteString("op", operation.Kind);
    writer.WriteString("file", operation.File);
    writer.WriteString("target", operation.Target);
    switch (operation)
    {
        case BadgeOperation badge:
        {
            writer.WriteString("badge", badge.Badge);
            writer.WriteNumber("size", badge.Style.Size);
            break;
        }

        case TagOperation tag:
        {
            writer.WriteString("color", tag.Color.ToString());
            writer.WriteNumber("size", tag.Size);
            break;
        }

        case OverlayOperation overlay:
        {
            writer.WriteString("image", overlay.Image);
            writer.WriteString("imageSha256", overlay.ImageSha256);
            break;
        }

        default:
        {
            throw new NotSupportedException(operation.Kind);
        }
    }

    writer.WriteString("corner", IconCorners.ToCode(operation.Corner));
    writer.WriteEndObject();
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
