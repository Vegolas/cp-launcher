using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Publishing;

/// <summary>A file of the build output, by its manifest-relative path ("assets/&lt;sha256&gt;.png").</summary>
public sealed record DistFile(string Path, byte[] Bytes);

/// <summary>Sheet of image pairs of one pack plus one numbered line per sheet cell (left to right, top to bottom).</summary>
/// <param name="Caption">What the two images of a pair are.</param>
public sealed record PackPreview(string PackId, RgbaImage Sheet, IReadOnlyList<string> Index, string Caption = PackPreview.BeforeAfter)
{
    public const string BeforeAfter = "before | after (after decoded from the encoded file)";
    public const string IconOverTarget = "icon | as shown over the target (decoded from the built LineageSkillEffect.u)";
    public const string SwappedSkills = "the two skills that swap effects";
}

/// <param name="Originals">SHA-256 of each original the manifest supports, by client file name.</param>
/// <param name="Notes">What the effect engine changed and checked, for the build output.</param>
public sealed record DistBuildResult(
    Manifest Manifest,
    byte[] ManifestBytes,
    IReadOnlyDictionary<string, string> Originals,
    IReadOnlyList<CompiledPack> Packs,
    IReadOnlyList<DistFile> Files,
    IReadOnlyList<PackPreview> Previews,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    /// <summary>SHA-256 of the original Icon.utx.</summary>
    public string OriginalSha256
    {
        get
        {
            return Originals[ClientFiles.IconUtx];
        }
    }
}

public sealed record DistBuildOptions
{
    /// <summary>Overrides the version from recipes.json.</summary>
    public string? Version { get; init; }

    public int PreviewScale { get; init; } = 4;

    public int PreviewColumns { get; init; } = 4;
}

/// <summary>
/// Compiles a recipes folder against the leader's original client files: manifest (checked by reading it back),
/// content-addressed assets, the optional launcher release and preview sheets. Every pack is also applied to the
/// originals together with all others, exactly as a launcher would, so missing textures or effect classes, overlaps
/// and encoding problems fail or warn here and not on the members' machines. Icon.utx is always needed (it also
/// holds the icons shown over the target); LineageSkillEffect.u and LineageEffect.u only when a pack changes them.
/// </summary>
public static class DistBuilder
{
    public static DistBuildResult Build(RecipeProject project, string originalPath, DistBuildOptions? options = null)
    {
        return Build(project, new[] { originalPath }, options);
    }

    /// <param name="originalPaths">The unmodified client files, each under its client name (Icon.utx, …).</param>
    public static DistBuildResult Build(RecipeProject project, IReadOnlyList<string> originalPaths, DistBuildOptions? options = null)
    {
        DistBuildOptions effective = options ?? new DistBuildOptions();
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in originalPaths)
        {
            string fileName = Path.GetFileName(path);
            if (!ClientFiles.IsPatchable(fileName))
            {
                throw new ArgumentException($"The original must be an unmodified client file {string.Join(" or ", ClientFiles.Patchable)} (same name), not '{fileName}'.");
            }

            string clientFile = ClientFiles.GetCanonicalName(fileName);
            if (originals.ContainsKey(clientFile))
            {
                throw new ArgumentException($"Two originals of {clientFile} were given.");
            }

            originals[clientFile] = File.ReadAllBytes(path);
        }

        CompiledRecipes compiled = RecipeCompiler.Compile(project);
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ClientFiles.IconUtx };
        foreach (IPatchOperation operation in compiled.Packs.SelectMany(pack => pack.Operations))
        {
            needed.Add(operation.File);
        }

        foreach (string file in ClientFiles.Patchable.Where(needed.Contains))
        {
            if (!originals.ContainsKey(file))
            {
                throw new ArgumentException($"The packs change {file}: give its unmodified original too (a folder with the originals, or the file itself).");
            }
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in ClientFiles.Patchable.Where(needed.Contains))
        {
            hashes[file] = ContentHash.Sha256Hex(originals[file]);
        }

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

        var manifest = new Manifest(
            ManifestParser.SupportedSchema,
            effective.Version ?? project.Version,
            project.Enabled,
            launcher,
            hashes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)new[] { pair.Value }),
            project.Announcements,
            compiled.Packs.Select(pack => pack.ToManifestPack()).ToList(),
            project.OptionPresets);
        byte[] manifestBytes = ManifestWriter.WriteChecked(manifest);

        var package = new IconPackage(Path.GetFileNameWithoutExtension(ClientFiles.IconUtx), L2File.FromBytes(originals[ClientFiles.IconUtx]));
        var assets = new MemoryAssets(compiled.Assets);
        var iconOperations = compiled.Packs.SelectMany(pack => pack.Operations).OfType<IIconOperation>().ToList();
        OperationBuildResult combined = OperationComposer.Build(package, iconOperations, assets);
        var warnings = new List<string>(combined.Warnings);
        var notes = new List<string>();
        var effectBuilds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ClientFiles.Patchable.Where(file => !ClientFiles.IsIconPackage(file) && needed.Contains(file)))
        {
            var effectOperations = compiled.Packs.SelectMany(pack => pack.Operations).OfType<IEffectOperation>().Where(operation => operation.File == file).ToList();
            EffectBuildResult effects = EffectComposer.Build(file, originals[file], effectOperations, () =>
            {
                return package;
            });
            effectBuilds[file] = effects.FileBytes;
            notes.AddRange(effects.Messages.Select(message => $"{file}: {message}"));
        }

        var previews = new List<PackPreview>();
        foreach (CompiledPack pack in compiled.Packs)
        {
            PackPreview? preview = BuildPreview(package, pack, assets, effectBuilds, effective);
            if (preview is not null)
            {
                previews.Add(preview);
            }
        }

        return new DistBuildResult(manifest, manifestBytes, hashes, compiled.Packs, files, previews, warnings, notes);
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

    /// <summary>
    /// Icon packs: before/after, the "after" decoded from the encoded file, so the sheet shows DXT1 artefacts too.
    /// Icons over the target: the icon, then the icon as the built LineageSkillEffect.u shows it (its stored texture
    /// flipped back). Effect swaps: the two skills' icons. The click arrow has no sheet.
    /// </summary>
    private static PackPreview? BuildPreview(
        IconPackage package,
        CompiledPack pack,
        IAssetStore assets,
        IReadOnlyDictionary<string, byte[]> effectBuilds,
        DistBuildOptions options)
    {
        var pairs = new List<PreviewPair>();
        var index = new List<string>();
        string caption = PackPreview.BeforeAfter;
        if (pack.Recipe.IsIconPack)
        {
            var operations = pack.Operations.OfType<IIconOperation>().ToList();
            OperationBuildResult build = OperationComposer.Build(package, operations, assets);
            var written = new IconPackage(package.PackageName, L2File.FromBytes(build.Replacement.FileBytes));
            foreach (RecipeEntry entry in pack.Entries)
            {
                string target = ((IIconOperation)entry.Operation).Target;
                RgbaImage before = package.Decode(package.FindTexture(target));
                RgbaImage after = written.Decode(written.FindTexture(target));
                pairs.Add(new PreviewPair(before, after));
                index.Add($"{index.Count + 1,3}. {entry.Summary}");
            }
        }
        else if (pack.Recipe.Type == RecipePackType.TargetIcon)
        {
            caption = PackPreview.IconOverTarget;
            UnrealPackage built = UnrealPackage.Parse(L2File.FromBytes(effectBuilds[ClientFiles.LineageSkillEffect]).Body);
            foreach (RecipeEntry entry in pack.Entries)
            {
                var icon = (TargetIconOperation)entry.Operation;
                RgbaImage before = package.Decode(package.FindTexture(EffectKinds.SkillIconPath(icon.Skill)));
                ExportEntry texture = built.Exports.Single(export => built.GetPath(export) == $"cp_icon_tex_{icon.Skill}");
                TextureData data = TextureReader.Read(built, texture);
                MipLevel mip = data.Mips[0];
                RgbaImage stored = Dxt1Codec.Decode(built.Data.AsSpan(mip.DataOffset, mip.DataSize), mip.Width, mip.Height);
                pairs.Add(new PreviewPair(before, FlipVertically(stored)));
                index.Add($"{index.Count + 1,3}. {entry.Summary}");
            }
        }
        else if (pack.Recipe.Type == RecipePackType.EffectSwap)
        {
            caption = PackPreview.SwappedSkills;
            foreach (RecipeEntry entry in pack.Entries)
            {
                var swap = (EffectSwapOperation)entry.Operation;
                RgbaImage first = package.Decode(package.FindTexture(EffectKinds.SkillIconPath(swap.First)));
                RgbaImage second = package.Decode(package.FindTexture(EffectKinds.SkillIconPath(swap.Second)));
                pairs.Add(new PreviewPair(first, second));
                index.Add($"{index.Count + 1,3}. {entry.Summary}");
            }
        }

        if (pairs.Count == 0)
        {
            return null;
        }

        return new PackPreview(pack.Recipe.Id, PreviewSheet.Build(pairs, options.PreviewScale, options.PreviewColumns), index, caption);
    }

    private static RgbaImage FlipVertically(RgbaImage image)
    {
        byte[] pixels = new byte[image.Pixels.Length];
        int rowBytes = image.Width * 4;
        for (int y = 0; y < image.Height; y++)
        {
            image.Pixels.AsSpan((image.Height - 1 - y) * rowBytes, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }

        return new RgbaImage(image.Width, image.Height, pixels);
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
