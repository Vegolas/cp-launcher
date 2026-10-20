using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Publishing;

/// <summary>A file of the build output, by its manifest-relative path ("assets/&lt;sha256&gt;.png").</summary>
public sealed record DistFile(string Path, byte[] Bytes);

/// <summary>Before/after sheet of one pack plus one numbered line per sheet cell (left to right, top to bottom).</summary>
public sealed record PackPreview(string PackId, RgbaImage Sheet, IReadOnlyList<string> Index);

public sealed record DistBuildResult(
    Manifest Manifest,
    byte[] ManifestBytes,
    string OriginalSha256,
    IReadOnlyList<CompiledPack> Packs,
    IReadOnlyList<DistFile> Files,
    IReadOnlyList<PackPreview> Previews,
    IReadOnlyList<string> Warnings);

public sealed record DistBuildOptions
{
    /// <summary>Overrides the version from recipes.json.</summary>
    public string? Version { get; init; }

    public int PreviewScale { get; init; } = 4;

    public int PreviewColumns { get; init; } = 4;
}

/// <summary>
/// Compiles a recipes folder against the leader's original client file: manifest (checked by reading it back),
/// content-addressed assets, the optional launcher release and one preview sheet per pack. All packs are also
/// applied together to the original, exactly as a launcher would, so missing textures, overlaps and encoding
/// problems fail or warn here and not on the members' machines.
/// </summary>
public static class DistBuilder
{
    public static DistBuildResult Build(RecipeProject project, string originalPath, DistBuildOptions? options = null)
    {
        DistBuildOptions effective = options ?? new DistBuildOptions();
        string fileName = Path.GetFileName(originalPath);
        if (!ClientFiles.IsPatchable(fileName))
        {
            throw new ArgumentException($"The original must be the unmodified client file {string.Join(" or ", ClientFiles.Patchable)} (same name), not '{fileName}'.");
        }

        string clientFile = ClientFiles.GetCanonicalName(fileName);
        byte[] originalBytes = File.ReadAllBytes(originalPath);
        string originalSha256 = ContentHash.Sha256Hex(originalBytes);
        var package = new IconPackage(Path.GetFileNameWithoutExtension(clientFile), L2File.FromBytes(originalBytes));

        CompiledRecipes compiled = RecipeCompiler.Compile(project);
        var files = new List<DistFile>();
        foreach (KeyValuePair<string, byte[]> asset in compiled.Assets.OrderBy(asset => asset.Key, StringComparer.Ordinal))
        {
            files.Add(new DistFile(asset.Key, asset.Value));
        }

        LauncherRelease? launcher = null;
        if (project.Launcher is not null)
        {
            launcher = AddLauncher(project.Launcher, files);
        }

        var packs = new List<Pack>();
        foreach (CompiledPack pack in compiled.Packs)
        {
            packs.Add(pack.ToManifestPack());
        }

        var manifest = new Manifest(
            ManifestParser.SupportedSchema,
            effective.Version ?? project.Version,
            project.Enabled,
            launcher,
            new Dictionary<string, IReadOnlyList<string>> { [clientFile] = new[] { originalSha256 } },
            project.Announcements,
            packs,
            project.OptionPresets);
        byte[] manifestBytes = ManifestWriter.WriteChecked(manifest);

        var assets = new MemoryAssets(compiled.Assets);
        var allOperations = compiled.Packs.SelectMany(pack => pack.Operations).ToList();
        OperationBuildResult combined = OperationComposer.Build(package, allOperations, assets);
        var warnings = new List<string>(combined.Warnings);
        var previews = new List<PackPreview>();
        foreach (CompiledPack pack in compiled.Packs)
        {
            previews.Add(BuildPreview(package, pack, assets, effective));
        }

        return new DistBuildResult(manifest, manifestBytes, originalSha256, compiled.Packs, files, previews, warnings);
    }

    private static LauncherRelease AddLauncher(RecipeLauncher recipe, List<DistFile> files)
    {
        if (!File.Exists(recipe.FilePath))
        {
            throw new FileNotFoundException($"Launcher {recipe.FilePath} not found.", recipe.FilePath);
        }

        byte[] bytes = File.ReadAllBytes(recipe.FilePath);
        string name = Path.GetFileNameWithoutExtension(recipe.FilePath) + "-" + recipe.Version + Path.GetExtension(recipe.FilePath);
        string url = $"{DistLayout.LauncherFolder}/{name}";
        files.Add(new DistFile(url, bytes));
        return new LauncherRelease(recipe.Version, url, ContentHash.Sha256Hex(bytes));
    }

    /// <summary>"After" images are decoded from the encoded file, so the sheet shows DXT1 artefacts too.</summary>
    private static PackPreview BuildPreview(IconPackage package, CompiledPack pack, IAssetStore assets, DistBuildOptions options)
    {
        OperationBuildResult build = OperationComposer.Build(package, pack.Operations, assets);
        var written = new IconPackage(package.PackageName, L2File.FromBytes(build.Replacement.FileBytes));
        var pairs = new List<PreviewPair>();
        var index = new List<string>();
        foreach (RecipeEntry entry in pack.Entries)
        {
            RgbaImage before = package.Decode(package.FindTexture(entry.Operation.Target));
            RgbaImage after = written.Decode(written.FindTexture(entry.Operation.Target));
            pairs.Add(new PreviewPair(before, after));
            index.Add($"{index.Count + 1,3}. {entry.Summary}");
        }

        return new PackPreview(pack.Recipe.Id, PreviewSheet.Build(pairs, options.PreviewScale, options.PreviewColumns), index);
    }

    private sealed class MemoryAssets : IAssetStore
    {
        private readonly IReadOnlyDictionary<string, byte[]> _assets;

        public MemoryAssets(IReadOnlyDictionary<string, byte[]> assets)
        {
            _assets = assets;
        }

        public byte[] ReadAsset(string path)
        {
            if (!_assets.TryGetValue(path, out byte[]? bytes))
            {
                throw new FileNotFoundException($"Asset {path} is not part of the build.");
            }

            return bytes;
        }
    }
}
