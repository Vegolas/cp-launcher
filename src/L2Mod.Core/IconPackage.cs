using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core;

public sealed record ReplacementResult(byte[] FileBytes, IReadOnlyList<BodyPatch> Patches, int ChangedBytes, IReadOnlyList<string> Warnings);

/// <summary>
/// New pixels for a texture; <see cref="Regions"/> limits re-encoding of mip 0 to the blocks they touch
/// (null re-encodes the whole texture).
/// </summary>
public sealed record TextureEdit(TextureData Texture, RgbaImage Image, IReadOnlyList<PixelRect>? Regions);

/// <summary>A texture package (.utx) opened for reading and size-preserving texture replacement.</summary>
public sealed class IconPackage
{
    public IconPackage(string path)
        : this(Path.GetFileNameWithoutExtension(path), L2File.Load(path))
    {
    }

    public IconPackage(string packageName, L2File file)
    {
        PackageName = packageName;
        SourceFile = file;
        Package = UnrealPackage.Parse(file.Body);
    }

    public string PackageName { get; }

    public L2File SourceFile { get; }

    public UnrealPackage Package { get; }

    /// <summary>Texture exports whose path contains <paramref name="filter"/>.</summary>
    public IEnumerable<ExportEntry> FindTextureExports(string filter)
    {
        foreach (ExportEntry export in Package.Exports)
        {
            if (!IsTexture(export))
            {
                continue;
            }

            string path = Package.GetPath(export);
            if (path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                yield return export;
            }
        }
    }

    public TextureData ReadTexture(ExportEntry export)
    {
        return TextureReader.Read(Package, export);
    }

    /// <summary>
    /// Finds a texture by object name ("skill1235"), group path ("skill_i.skill1235") or the
    /// package-qualified form used by Skillgrp.dat ("icon.skill1235", groups omitted).
    /// Object names may themselves contain dots, so whole names are compared rather than split.
    /// </summary>
    public TextureData FindTexture(string name)
    {
        var matches = new List<ExportEntry>();
        foreach (ExportEntry export in Package.Exports)
        {
            if (IsTexture(export) && MatchesName(export, name))
            {
                matches.Add(export);
            }
        }

        if (matches.Count == 0)
        {
            throw new KeyNotFoundException($"Texture '{name}' not found in {PackageName}.");
        }

        if (matches.Count > 1)
        {
            string candidates = string.Join(", ", matches.Select(Package.GetPath));
            throw new InvalidOperationException($"Texture '{name}' is ambiguous: {candidates}. Use the group path.");
        }

        return TextureReader.Read(Package, matches[0]);
    }

    public RgbaImage Decode(TextureData texture)
    {
        MipLevel mip = texture.Mips[0];
        return Dxt1Codec.Decode(Package.Data.AsSpan(mip.DataOffset, mip.DataSize), mip.Width, mip.Height);
    }

    /// <summary>
    /// Encodes <paramref name="source"/> into every mip of the texture and returns the patched file bytes.
    /// The source must match the texture size or be an integer multiple of it (it is box-downscaled).
    /// </summary>
    public ReplacementResult BuildReplacement(TextureData texture, RgbaImage source)
    {
        return BuildBatch(new[] { new TextureEdit(texture, source, null) });
    }

    /// <summary>
    /// Applies several texture edits to one output file. When an edit has regions and needs no scaling,
    /// only the DXT1 blocks of mip 0 touching those regions are re-encoded.
    /// </summary>
    public ReplacementResult BuildBatch(IReadOnlyList<TextureEdit> edits)
    {
        var warnings = new List<string>();
        var patches = new List<BodyPatch>();
        var patchedOffsets = new HashSet<int>();
        foreach (TextureEdit edit in edits)
        {
            foreach (BodyPatch patch in EncodePatches(edit, warnings))
            {
                if (!patchedOffsets.Add(patch.BodyOffset))
                {
                    throw new InvalidOperationException($"{edit.Texture.Path} is edited more than once.");
                }

                patches.Add(patch);
            }
        }

        byte[] fileBytes = SourceFile.BuildPatchedFile(patches);
        int changedBytes = CountChangedBytes(SourceFile.RawBytes, fileBytes);
        return new ReplacementResult(fileBytes, patches, changedBytes, warnings);
    }

    /// <summary>
    /// Re-reads patched bytes from scratch and checks that the edited textures still parse, that every
    /// patch reads back exactly, and that nothing outside the patched ranges changed.
    /// </summary>
    public void VerifyReplacement(ReplacementResult result, IEnumerable<string> textureNames)
    {
        if (result.FileBytes.Length != SourceFile.RawBytes.Length)
        {
            throw new InvalidDataException("Patched file size differs from the original.");
        }

        var reopened = new IconPackage(PackageName, L2File.FromBytes(result.FileBytes));
        foreach (string textureName in textureNames)
        {
            reopened.FindTexture(textureName);
        }

        foreach (BodyPatch patch in result.Patches)
        {
            ReadOnlySpan<byte> stored = reopened.Package.Data.AsSpan(patch.BodyOffset, patch.Bytes.Length);
            if (!stored.SequenceEqual(patch.Bytes))
            {
                throw new InvalidDataException($"Data at body offset {patch.BodyOffset} does not read back as written.");
            }
        }

        for (int offset = 0; offset < result.FileBytes.Length; offset++)
        {
            if (result.FileBytes[offset] != SourceFile.RawBytes[offset] && !IsInsidePatch(offset, result.Patches))
            {
                throw new InvalidDataException($"Unexpected change at file offset {offset}.");
            }
        }
    }

    private List<BodyPatch> EncodePatches(TextureEdit edit, List<string> warnings)
    {
        TextureData texture = edit.Texture;
        RgbaImage source = edit.Image;
        int factor = GetScaleFactor(texture, source);
        if (factor > 1)
        {
            warnings.Add($"{texture.Path}: source {source.Width}x{source.Height} downscaled by {factor} to {texture.Width}x{texture.Height}.");
        }

        if (source.HasTransparency())
        {
            warnings.Add($"{texture.Path}: source has transparent pixels; DXT1 output is opaque, alpha was ignored.");
        }

        RgbaImage baseImage = source.DownscaleBox(factor);
        var patches = new List<BodyPatch>();
        for (int i = 0; i < texture.Mips.Count; i++)
        {
            MipLevel mip = texture.Mips[i];
            RgbaImage mipImage = baseImage.DownscaleBox(baseImage.Width / mip.Width);
            byte[] encoded;
            if (i == 0 && edit.Regions is not null)
            {
                ReadOnlySpan<byte> baseline = Package.Data.AsSpan(mip.DataOffset, mip.DataSize);
                encoded = Dxt1Codec.EncodeRegion(mipImage, baseline, edit.Regions);
            }
            else
            {
                encoded = Dxt1Codec.Encode(mipImage);
            }

            if (encoded.Length != mip.DataSize)
            {
                throw new InvalidOperationException($"Encoded mip {mip.Width}x{mip.Height} has unexpected size {encoded.Length}.");
            }

            patches.Add(new BodyPatch(mip.DataOffset, encoded));
        }

        return patches;
    }

    private bool IsTexture(ExportEntry export)
    {
        return string.Equals(Package.GetClassName(export), "Texture", StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesName(ExportEntry export, string name)
    {
        string objectName = Package.GetName(export.ObjectNameIndex);
        string path = Package.GetPath(export);
        var accepted = new[]
        {
            objectName,
            path,
            PackageName + "." + objectName,
            PackageName + "." + path,
        };

        foreach (string candidate in accepted)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInsidePatch(int fileOffset, IReadOnlyList<BodyPatch> patches)
    {
        foreach (BodyPatch patch in patches)
        {
            int start = SourceFile.ToFileOffset(patch.BodyOffset);
            if (fileOffset >= start && fileOffset < start + patch.Bytes.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetScaleFactor(TextureData texture, RgbaImage source)
    {
        bool divisible = source.Width % texture.Width == 0 && source.Height % texture.Height == 0;
        if (divisible && source.Width / texture.Width == source.Height / texture.Height)
        {
            return source.Width / texture.Width;
        }

        throw new ArgumentException(
            $"Image is {source.Width}x{source.Height}; it must be {texture.Width}x{texture.Height} or an integer multiple of it.");
    }

    private static int CountChangedBytes(byte[] original, byte[] patched)
    {
        int count = 0;
        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] != patched[i])
            {
                count++;
            }
        }

        return count;
    }
}
