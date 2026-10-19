using L2Mod.Cli;
using L2Mod.Core;
using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;
using L2Mod.Core.Recipes;
using L2Mod.Core.Textures;

const int ExitOk = 0;
const int ExitError = 1;
const int ExitUsage = 2;
const int DefaultPreviewScale = 4;
const int PreviewColumns = 4;

if (args.Length == 0)
{
    PrintUsage();
    return ExitUsage;
}

try
{
    string[] rest = args.Skip(1).ToArray();
    switch (args[0].ToLowerInvariant())
    {
        case "list":
            return RunList(CommandLine.Parse(rest, Array.Empty<string>(), Array.Empty<string>()));
        case "export":
            return RunExport(CommandLine.Parse(rest, new[] { "scale" }, Array.Empty<string>()));
        case "replace":
            return RunReplace(CommandLine.Parse(rest, Array.Empty<string>(), new[] { "overwrite" }));
        case "badge":
            return RunBadge(CommandLine.Parse(rest, new[] { "size", "border", "border-color", "corner", "crop", "preview", "preview-scale" }, new[] { "overwrite" }));
        default:
            PrintUsage();
            return ExitUsage;
    }
}
catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException
    or KeyNotFoundException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
    or FormatException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return ExitError;
}

int RunList(CommandLine command)
{
    if (command.Positional.Count != 2)
    {
        PrintUsage();
        return ExitUsage;
    }

    var package = new IconPackage(command.Positional[0]);
    PrintPackageInfo(package);
    int count = 0;
    foreach (ExportEntry export in package.FindTextureExports(command.Positional[1]))
    {
        Console.WriteLine($"  {package.Package.GetPath(export)}  {DescribeTexture(package, export)}");
        count++;
    }

    Console.WriteLine($"{count} texture(s) matching '{command.Positional[1]}'.");
    return ExitOk;
}

int RunExport(CommandLine command)
{
    if (command.Positional.Count != 3)
    {
        PrintUsage();
        return ExitUsage;
    }

    int scale = command.GetInt("scale", 1, 1, 32);
    var package = new IconPackage(command.Positional[0]);
    TextureData texture = package.FindTexture(command.Positional[1]);
    RgbaImage image = package.Decode(texture).UpscaleNearest(scale);
    PngCodec.Save(image, command.Positional[2]);
    Console.WriteLine($"Exported {texture.Path} ({texture.Width}x{texture.Height} DXT1, {texture.Mips.Count} mip(s)) to {command.Positional[2]}.");
    return ExitOk;
}

int RunReplace(CommandLine command)
{
    if (command.Positional.Count != 4)
    {
        PrintUsage();
        return ExitUsage;
    }

    string outputPath = CheckOutputPath(command.Positional[0], command.Positional[3], command.HasFlag("overwrite"));
    var package = new IconPackage(Path.GetFullPath(command.Positional[0]));
    TextureData texture = package.FindTexture(command.Positional[1]);
    RgbaImage source = PngCodec.Load(command.Positional[2]);
    ReplacementResult result = package.BuildReplacement(texture, source);
    package.VerifyReplacement(result, new[] { command.Positional[1] });
    WriteOutput(outputPath, result.FileBytes);

    PrintPackageInfo(package);
    Console.WriteLine($"Replaced {texture.Path} ({texture.Width}x{texture.Height}, {texture.Mips.Count} mip(s)).");
    foreach (BodyPatch patch in result.Patches)
    {
        Console.WriteLine($"  patched {patch.Bytes.Length} bytes at file offset {package.SourceFile.ToFileOffset(patch.BodyOffset)}");
    }

    PrintResultSummary(package, result.Warnings, result, outputPath);
    return ExitOk;
}

int RunBadge(CommandLine command)
{
    if (command.Positional.Count != 3)
    {
        PrintUsage();
        return ExitUsage;
    }

    var style = new BadgeStyle(
        command.GetInt("size", BadgeStyle.Default.Size, 4, 32),
        IconCorners.Parse(command.GetOption("corner") ?? "br"),
        command.GetInt("border", BadgeStyle.Default.Border, 0, 4),
        command.GetInt("crop", BadgeStyle.Default.Crop, 0, 64))
    {
        BorderColor = Rgb.ParseHex(command.GetOption("border-color") ?? "000000"),
    };
    string outputPath = CheckOutputPath(command.Positional[0], command.Positional[2], command.HasFlag("overwrite"));
    var package = new IconPackage(Path.GetFullPath(command.Positional[0]));
    IReadOnlyList<BadgeMappingEntry> mapping = BadgeMapping.Load(command.Positional[1]);
    IReadOnlyList<IIconOperation> operations = MappingOperations.FromBadges(mapping, package.PackageName + ".utx", style);

    OperationBuildResult build = OperationComposer.Build(package, operations, null);
    ReplacementResult result = build.Replacement;
    WriteOutput(outputPath, result.FileBytes);

    PrintPackageInfo(package);
    Console.WriteLine($"Badge {style.Size}px (border {style.Border} {style.BorderColor}, crop {style.Crop}) in corner {style.Corner}; {(style.Size % 4 == 0 ? "aligned" : "NOT aligned")} to DXT1 blocks.");
    for (int i = 0; i < mapping.Count; i++)
    {
        Console.WriteLine($"  {i + 1,2}. {package.FindTexture(mapping[i].Target).Path} <- {mapping[i].Badge}  {mapping[i].Label}");
    }

    PrintResultSummary(package, build.Warnings, result, outputPath);
    string? previewPath = command.GetOption("preview");
    if (previewPath is not null)
    {
        var written = new IconPackage(package.PackageName, L2File.FromBytes(result.FileBytes));
        var pairs = new List<PreviewPair>();
        for (int i = 0; i < mapping.Count; i++)
        {
            RgbaImage before = package.Decode(package.FindTexture(mapping[i].Target));
            RgbaImage after = written.Decode(written.FindTexture(mapping[i].Target));
            pairs.Add(new PreviewPair(before, after));
        }

        int previewScale = command.GetInt("preview-scale", DefaultPreviewScale, 1, 16);
        PngCodec.Save(PreviewSheet.Build(pairs, previewScale, PreviewColumns), previewPath);
        Console.WriteLine($"Preview (before | after, decoded from the written file, numbered left to right) saved to {previewPath}.");
    }

    return ExitOk;
}

string CheckOutputPath(string input, string output, bool overwrite)
{
    string inputPath = Path.GetFullPath(input);
    string outputPath = Path.GetFullPath(output);
    if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Output must be a different file than the input package.");
    }

    if (File.Exists(outputPath) && !overwrite)
    {
        throw new IOException($"{outputPath} already exists. Pass --overwrite to replace it.");
    }

    return outputPath;
}

void WriteOutput(string outputPath, byte[] bytes)
{
    string? directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllBytes(outputPath, bytes);
}

void PrintResultSummary(IconPackage package, IReadOnlyList<string> warnings, ReplacementResult result, string outputPath)
{
    Console.WriteLine($"  {result.ChangedBytes} byte(s) differ from the original, file size unchanged; verified by re-reading.");
    foreach (string warning in warnings)
    {
        Console.WriteLine($"  warning: {warning}");
    }

    WarnAboutKey(package, outputPath);
    Console.WriteLine($"Written {outputPath}.");
}

void WarnAboutKey(IconPackage package, string outputPath)
{
    if (package.SourceFile.Encryption != L2Encryption.Ver121)
    {
        return;
    }

    string outputName = Path.GetFileName(outputPath);
    if (L2File.DeriveVer121Key(outputName) != package.SourceFile.Key)
    {
        Console.WriteLine($"  note: the client derives the Ver121 key from the file name; install this file as '{package.PackageName}.utx', not '{outputName}'.");
    }
}

void PrintPackageInfo(IconPackage package)
{
    string encryption = package.SourceFile.Encryption.ToString();
    if (package.SourceFile.Encryption != L2Encryption.None)
    {
        encryption += $" (key 0x{package.SourceFile.Key:X2})";
    }

    Console.WriteLine($"{package.PackageName}: UE2 v{package.Package.FileVersion}/{package.Package.LicenseeVersion}, {encryption}, {package.Package.Exports.Count} exports");
}

string DescribeTexture(IconPackage package, ExportEntry export)
{
    try
    {
        TextureData texture = package.ReadTexture(export);
        return $"{texture.Width}x{texture.Height} {TextureFormats.GetName(texture.Format)}, {texture.Mips.Count} mip(s)";
    }
    catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or InvalidOperationException)
    {
        return $"(not replaceable: {exception.Message})";
    }
}

void PrintUsage()
{
    Console.WriteLine("L2Mod - edit DXT1 textures in Lineage 2 .utx packages (Ver111/Ver121/plain), size-preserving.");
    Console.WriteLine();
    Console.WriteLine("  list    <package.utx> <filter>");
    Console.WriteLine("  export  <package.utx> <texture> <output.png> [--scale N]");
    Console.WriteLine("  replace <package.utx> <texture> <image.png> <output.utx> [--overwrite]");
    Console.WriteLine("  badge   <package.utx> <mapping.txt> <output.utx> [--size 16] [--border 1] [--border-color 000000] [--crop 0] [--corner br|bl|tr|tl]");
    Console.WriteLine("          [--preview sheet.png] [--preview-scale 4] [--overwrite]");
    Console.WriteLine();
    Console.WriteLine("Texture names: skill1235, skill_i.skill1235 or icon.skill1235 (as in Skillgrp.dat).");
    Console.WriteLine("badge mapping: one '<target> <badge>  # label' per line; always run it on the ORIGINAL package.");
    Console.WriteLine("Output never overwrites the input; keep the original file name when installing (Ver121 key).");
}
