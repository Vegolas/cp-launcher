using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Effects;

/// <summary>Size, position and lifetime of a sprite shown over the target.</summary>
/// <param name="HalfSize">Sprite half-size in world units.</param>
/// <param name="Height">Sprite centre at spawn, above the top of the target's collision cylinder.</param>
/// <param name="RiseSpeed">The sprite floats up this many units per second.</param>
public sealed record SpriteStyle(float HalfSize, float Height, float RiseSpeed, float Seconds)
{
    /// <summary>A skill icon: starts above the head, floats up and fades out.</summary>
    public static SpriteStyle Icon(int seconds)
    {
        return new SpriteStyle(10f, 40f, 8f, seconds);
    }
}

/// <summary>
/// Adds icons over the target to LineageSkillEffect.u (append-only, see <see cref="PackageEditor"/>). Per icon, a new
/// effect class cp_icon_&lt;id&gt; (an Emitter with one camera-facing sprite, following the target) and its own
/// texture (a vertically flipped copy of the skill's icon, laid out like the textures of the icon package it comes
/// from) are added, and a new L2EffectEmitter for it is appended to the skill class the game plays. The class layout
/// and the property values follow LineageEffect.u's effect classes, e.g. a_u011_death_penalty, and L2Dex's dex_zone_*
/// icon sprites.
/// </summary>
public sealed class TargetIconBuilder
{
    private const uint ClassExportFlags = 0x000E0004;
    private const uint TextBufferFlags = 0x00240000;
    private const uint SubobjectFlags = 0x00060000;
    private const uint EmitterClassFlags = 0x212;
    private const ulong EmitterProbeMask = 0x0080000000000040;
    private const uint EmitterScriptCrc = 0x92E2C039; // Engine.Emitter as recorded by LineageEffect.u's classes
    private const byte AttachTrail = 9;               // L2EffectEmitter.AttachMethod AM_Trail: follows the actor
    private const byte CoordinatesRelative = 1;       // PTCS_Relative: the particle moves with the emitter
    private const byte DrawAlphaBlend = 1;            // PTDS_AlphaBlend
    private const string PackageName = "LineageSkillEffect";

    private readonly byte[] _before;
    private readonly IconPackage _icons;
    private readonly PackageEditor _editor;
    private readonly IReadOnlyList<(int Ref, int Number)> _skillClasses;
    private readonly List<TargetIconOperation> _added = new List<TargetIconOperation>();
    private readonly List<string> _messages = new List<string>();
    private readonly int _objectClass;
    private readonly int _textBufferClass;
    private readonly int _emitterClass;
    private readonly int _spriteEmitterClass;
    private readonly int _effectEmitterClass;
    private readonly int _textureClass;
    private int _markers;

    /// <param name="original">The client's LineageSkillEffect.u as read (only its encryption is checked).</param>
    /// <param name="package">The package bytes to add to, e.g. after effect swaps.</param>
    /// <param name="icons">The original icon package (Icon.utx, same package version): the icon pixels and the
    /// layout of each new texture. Only read.</param>
    public TargetIconBuilder(L2File original, byte[] package, IconPackage icons)
    {
        if (original.Encryption != L2Encryption.None)
        {
            throw new NotSupportedException("Icons can only be added to an unencrypted LineageSkillEffect.u (L2Dex's is one).");
        }

        if (icons.Package.FileVersion != UnrealPackage.Parse(package).FileVersion)
        {
            throw new InvalidDataException($"{icons.PackageName}.utx is not the same package version as LineageSkillEffect.u.");
        }

        _before = package;
        _icons = icons;
        _editor = new PackageEditor(package);
        int corePackage = _editor.Import("Core", "Package", 0, "Core");
        int enginePackage = _editor.Import("Core", "Package", 0, "Engine");
        _objectClass = _editor.Import("Core", "Class", corePackage, "Object");
        _textBufferClass = _editor.Import("Core", "Class", corePackage, "TextBuffer");
        _emitterClass = _editor.Import("Core", "Class", enginePackage, "Emitter");
        _spriteEmitterClass = _editor.Import("Core", "Class", enginePackage, "SpriteEmitter");
        _effectEmitterClass = _editor.Import("Core", "Class", enginePackage, "L2EffectEmitter");
        _editor.Import("Core", "Class", enginePackage, "L2SkillEffect");
        _textureClass = _editor.Import("Core", "Class", enginePackage, "Texture");

        // Skill classes as they were before anything was added (markers go on these).
        _skillClasses = _editor.TopLevelClasses()
            .Select(item => (item.Ref, Number: SkillClassNumber(item.Name)))
            .Where(item => item.Number > 0)
            .ToList();
    }

    /// <summary>The numbers of the l2_&lt;number&gt;_skill classes the package had before anything was added.</summary>
    public IReadOnlyList<int> SkillClassNumbers
    {
        get
        {
            return _skillClasses.Select(item => item.Number).ToList();
        }
    }

    /// <summary>What was added where, for build output.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            return _messages;
        }
    }

    public void AddIcon(TargetIconOperation operation)
    {
        if (_added.Any(item => item.Skill == operation.Skill || item.EffectClass == operation.EffectClass))
        {
            throw new ArgumentException($"Skill {operation.Skill} or effect class {operation.EffectClass} has an icon already.");
        }

        string skillClassName = EffectKinds.SkillClassName(operation.EffectClass);
        int skillClass = _editor.FindClass(skillClassName);
        if (skillClass == 0)
        {
            // A new class for a skill that has none is never played: the client picks its effect elsewhere
            // (in-game test 2026-11-08, Noblesse Blessing and Slow). The recipe has to name the class it plays.
            throw new InvalidDataException($"LineageSkillEffect.u has no effect class {skillClassName}; name the class the skill plays (@<id>).");
        }

        string iconPath = EffectKinds.SkillIconPath(operation.Skill);
        ExportEntry layout = FindTexture(iconPath)
            ?? throw new InvalidDataException($"{_icons.PackageName}.utx has no icon {iconPath} for skill {operation.Skill}.");
        TextureData source = TextureReader.Read(_icons.Package, layout);
        MipLevel mip = source.Mips[0];
        byte[] flipped = FlipDxt1(_icons.Package.Data.AsSpan(mip.DataOffset, mip.DataSize), source.Width, source.Height);
        int iconClass = AddSpriteClass($"cp_icon_{operation.Skill}", $"cp_icon_tex_{operation.Skill}", layout, iconPath, flipped, SpriteStyle.Icon(operation.Seconds));
        string action = AttachToSkillClass(skillClass, iconClass);
        if (operation.EffectClass != operation.Skill || action != "ShotAction")
        {
            _messages.Add($"{operation.Skill}: icon added to {skillClassName}.{action}");
        }

        _added.Add(operation);
    }

    /// <summary>
    /// Shows <paramref name="image"/> (32x32) over the target whenever l2_&lt;<paramref name="skillClassNumber"/>&gt;_skill
    /// plays; for finding out in game which class a skill plays (probe builds, never published).
    /// </summary>
    /// <param name="layoutIcon">A 32x32 one-mip icon of the icon package whose layout the marker texture copies.</param>
    public void AddMarker(int skillClassNumber, RgbaImage image, string layoutIcon, SpriteStyle style)
    {
        int skillClass = _skillClasses.FirstOrDefault(item => item.Number == skillClassNumber).Ref;
        if (skillClass == 0)
        {
            throw new ArgumentException($"There is no class l2_{skillClassNumber}_skill to mark.");
        }

        ExportEntry layout = FindTexture(layoutIcon) ?? throw new InvalidDataException($"No {layoutIcon} for the marker textures.");
        byte[] flipped = FlipDxt1(Dxt1Codec.Encode(image), image.Width, image.Height);
        int markerClass = AddSpriteClass($"cp_probe_{skillClassNumber}", $"cp_probe_tex_{skillClassNumber}", layout, layoutIcon, flipped, style);
        AttachToSkillClass(skillClass, markerClass);
        _markers++;
    }

    /// <summary>The package with everything added, checked: every class decodes exactly, every new texture reads
    /// back and every icon is its source flipped pixel for pixel.</summary>
    public byte[] Build()
    {
        byte[] output = _editor.Build();
        Verify(output);
        return output;
    }

    /// <summary>Stores the DXT1 data flipped vertically (sprites draw textures upside down): block rows in reverse
    /// order and the four pixel rows inside each block reversed, which is lossless.</summary>
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

    private ExportEntry? FindTexture(string path)
    {
        UnrealPackage package = _icons.Package;
        return package.Exports.FirstOrDefault(e => package.GetClassName(e) == "Texture" && package.GetPath(e) == path);
    }

    /// <summary>l2_&lt;number&gt;_skill -> number; 0 for any other name.</summary>
    private static int SkillClassNumber(string name)
    {
        if (!name.StartsWith("l2_", StringComparison.Ordinal) || !name.EndsWith("_skill", StringComparison.Ordinal))
        {
            return 0;
        }

        string digits = name.Substring(3, name.Length - 9);
        if (digits.Length is < 1 or > 5 || !digits.All(char.IsAsciiDigit))
        {
            return 0;
        }

        int number = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (!EffectKinds.IsValidSkillId(number))
        {
            return 0;
        }

        return number;
    }

    /// <summary>An Emitter class with one camera-facing sprite showing a 32x32 texture (stored flipped).</summary>
    private int AddSpriteClass(string className, string textureName, ExportEntry layout, string layoutPath, byte[] flippedDxt1, SpriteStyle style)
    {
        if (_editor.FindClass(className) != 0)
        {
            throw new InvalidDataException($"{className} already exists: build from the original package.");
        }

        byte[] textureData = IconTextureObject.Build(
            _editor, _icons.Package, layout, $"{_icons.PackageName}.{layoutPath}", flippedDxt1, $"{PackageName}.{textureName}", out int skipAt, out int skipValue);
        int texture = _editor.AddExport(_textureClass, 0, 0, textureName, layout.ObjectFlags, textureData);
        _editor.AddOffsetFixup(texture, skipAt, skipValue);
        int spriteClass = _editor.AddExport(0, _emitterClass, 0, className, ClassExportFlags);
        int text = _editor.AddExport(_textBufferClass, 0, spriteClass, "ScriptText", TextBufferFlags, EmptyTextBuffer());
        int sprite = _editor.AddExport(_spriteEmitterClass, 0, spriteClass, "SpriteEmitter0", SubobjectFlags, SpriteDefaults(texture, style));
        _editor.SetData(spriteClass, ClassData(spriteClass, _emitterClass, text, _objectClass, EmitterProbeMask, EmitterClassFlags, EmitterScriptCrc, tags =>
        {
            tags.ObjectArray("Emitters", new[] { sprite });
            tags.Bool("bLightChanged", true);
            tags.Bool("bNoDelete", false);
        }));
        return spriteClass;
    }

    /// <summary>Appends an L2EffectEmitter for effectClass where the skill class already plays its target effects:
    /// ShotAction, or CastingAction for classes that have no ShotAction (e.g. l2_6673_skill), else a new ShotAction.</summary>
    private string AttachToSkillClass(int skillClass, int effectClass)
    {
        int effect = _editor.AddExport(_effectEmitterClass, 0, skillClass, NextEmitterName(skillClass), SubobjectFlags, EffectEmitterData(effectClass));
        byte[] data = _editor.GetData(skillClass);
        IReadOnlyList<PropertyTag> tags = PropertyTags.Read(data, ClassLayout.DefaultsOffset(data), data.Length, _editor.NameAt);
        string action = "CastingAction";
        if (tags.Any(t => t.Name == "ShotAction" && t.Type == PropertyTypes.Array) || !tags.Any(t => t.Name == "CastingAction" && t.Type == PropertyTypes.Array))
        {
            action = "ShotAction";
        }

        _editor.SetData(skillClass, AppendToAction(skillClass, action, effect));
        return action;
    }

    private byte[] EmptyTextBuffer()
    {
        var writer = new ByteWriter();
        writer.Compact(_editor.Name("None"));
        writer.Int32(0);
        writer.Int32(0);
        writer.Compact(0);
        return writer.ToArray();
    }

    /// <summary>A script-less UClass as this package version stores it, followed by its tagged defaults.</summary>
    private byte[] ClassData(int classRef, int superClass, int scriptText, int within, ulong probeMask, uint classFlags, uint superScriptCrc, Action<TagWriter> defaults)
    {
        var writer = new ByteWriter();
        writer.Compact(superClass);                                // UField.SuperField
        writer.Compact(0);                                         // UField.Next
        writer.Compact(scriptText);                                // UStruct.ScriptText
        writer.Compact(0);                                         // UStruct.Children
        writer.Compact(_editor.Name(_editor.ExportName(classRef))); // UStruct.FriendlyName
        writer.Compact(0);                                         // UStruct.CppText
        writer.Int32(-1);                                          // Line
        writer.Int32(-1);                                          // TextPos
        writer.Int32(0);                                           // ScriptSize
        writer.UInt64(probeMask);                                  // UState.ProbeMask
        writer.UInt64(ulong.MaxValue);                             // UState.IgnoreMask
        writer.UInt16(0xFFFF);                                     // UState.LabelTableOffset
        writer.UInt32(0);                                          // UState.StateFlags
        writer.UInt32(classFlags);                                 // UClass.ClassFlags
        writer.Bytes(new byte[16]);                                // UClass.ClassGuid
        writer.Compact(2);                                         // Dependencies: the class itself and its super class
        writer.Compact(classRef);
        writer.Int32(1);
        writer.UInt32(0);
        writer.Compact(superClass);
        writer.Int32(1);
        writer.UInt32(superScriptCrc);
        writer.Compact(3);                                         // PackageImports
        writer.Compact(_editor.Name("LineageSkillEffect"));
        writer.Compact(_editor.Name("Engine"));
        writer.Compact(_editor.Name("Core"));
        writer.Compact(within);                                    // ClassWithin
        writer.Compact(_editor.Name("System"));                    // ClassConfigName
        writer.Compact(0);                                         // HideCategories
        var tags = new TagWriter(_editor, writer);
        defaults(tags);
        tags.None();
        return writer.ToArray();
    }

    private byte[] SpriteDefaults(int texture, SpriteStyle style)
    {
        var writer = new ByteWriter();
        var tags = new TagWriter(_editor, writer);
        tags.Float("FadeOutStartTime", Math.Max(style.Seconds - 1f, style.Seconds / 2f));
        tags.Bool("FadeOut", true);
        tags.Float("FadeInEndTime", 0.15f);
        tags.Bool("FadeIn", true);
        tags.Byte("CoordinateSystem", CoordinatesRelative);
        tags.Int("MaxParticles", 1);
        tags.Bool("RespawnDeadParticles", false);
        tags.Vector("StartLocationOffset", 0f, 0f, style.Height);
        tags.Bool("UniformSize", true);
        tags.RangeVector("StartSizeRange", style.HalfSize, style.HalfSize, style.HalfSize);
        tags.RangeVector("StartVelocityRange", 0f, 0f, style.RiseSpeed);
        tags.Float("InitialParticlesPerSecond", 1000f);
        tags.Bool("AutomaticInitialSpawning", false);
        tags.Byte("DrawStyle", DrawAlphaBlend);
        tags.Object("Texture", texture);
        tags.Float("SecondsBeforeInactive", 0f);
        tags.Range("LifetimeRange", style.Seconds, style.Seconds);
        tags.None();
        return writer.ToArray();
    }

    /// <summary>Like Surrender's own on-target effects: on the target, following it; offset is relative to its
    /// collision cylinder (Z 1 = top of the head). bAdjustLifeTime off so the engine keeps the sprite's own lifetime.</summary>
    private byte[] EffectEmitterData(int effectClass)
    {
        var writer = new ByteWriter();
        var tags = new TagWriter(_editor, writer);
        tags.Byte("AttachOn", AttachTrail);
        tags.Vector("offset", 0f, 0f, 1f);
        tags.Bool("bOnMultiTarget", true);
        tags.Bool("bAdjustLifeTime", false);
        tags.Object("EffectClass", effectClass);
        tags.Object("SimpleEffectClass", effectClass);
        tags.None();
        return writer.ToArray();
    }

    private string NextEmitterName(int skillClass)
    {
        const string Prefix = "L2EffectEmitter";
        int next = 0;
        foreach (string name in _editor.ChildNames(skillClass))
        {
            if (name.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(name.Substring(Prefix.Length), out int number))
            {
                next = Math.Max(next, number + 1);
            }
        }

        return $"{Prefix}{next}";
    }

    /// <summary>The skill class's defaults with the new emitter appended to the given action array (added when missing).</summary>
    private byte[] AppendToAction(int skillClass, string action, int effect)
    {
        byte[] data = _editor.GetData(skillClass);
        int defaults = ClassLayout.DefaultsOffset(data);
        var writer = new ByteWriter();
        writer.Bytes(data.AsSpan(0, defaults));
        var tags = new TagWriter(_editor, writer);
        bool done = false;
        foreach (PropertyTag tag in PropertyTags.Read(data, defaults, data.Length, _editor.NameAt))
        {
            if (tag.Name == action && tag.Type == PropertyTypes.Array)
            {
                int o = tag.ValueOffset;
                int count = CompactIndex.Read(data, ref o);
                var references = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    references.Add(CompactIndex.Read(data, ref o));
                }

                references.Add(effect);
                tags.ObjectArray(action, references);
                done = true;
            }
            else
            {
                writer.Bytes(data.AsSpan(tag.Start, tag.End - tag.Start));
            }
        }

        if (!done)
        {
            tags.ObjectArray(action, new[] { effect });
        }

        tags.None();
        return writer.ToArray();
    }

    private void Verify(byte[] after)
    {
        UnrealPackage old = UnrealPackage.Parse(_before);
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

        int classes = 0;
        int textures = 0;
        foreach (ExportEntry export in check.Exports)
        {
            if (export.ClassIndex == 0)
            {
                byte[] data = check.Data.AsSpan(export.SerialOffset, export.SerialSize).ToArray();
                PropertyTags.Read(data, ClassLayout.DefaultsOffset(data), data.Length, check.GetName, out int next);
                if (next != data.Length)
                {
                    throw new InvalidDataException($"{check.GetPath(export)}: defaults do not end exactly at the end of the object.");
                }

                classes++;
            }
            else if (check.GetClassName(export) == "Texture" && check.GetName(export.ObjectNameIndex).StartsWith("cp_", StringComparison.Ordinal))
            {
                if (TextureReader.Read(check, export).Mips.Count != 1)
                {
                    throw new InvalidDataException($"{check.GetPath(export)}: expected one mip.");
                }

                textures++;
            }
        }

        foreach (TargetIconOperation icon in _added)
        {
            TextureData ours = TextureReader.Read(check, check.Exports.Single(e => check.GetPath(e) == $"cp_icon_tex_{icon.Skill}"));
            ExportEntry sourceExport = FindTexture(EffectKinds.SkillIconPath(icon.Skill))!;
            TextureData source = TextureReader.Read(_icons.Package, sourceExport);
            RgbaImage flipped = Dxt1Codec.Decode(check.Data.AsSpan(ours.Mips[0].DataOffset, ours.Mips[0].DataSize), ours.Width, ours.Height);
            RgbaImage image = Dxt1Codec.Decode(_icons.Package.Data.AsSpan(source.Mips[0].DataOffset, source.Mips[0].DataSize), source.Width, source.Height);
            int rowBytes = image.Width * 4;
            for (int y = 0; y < image.Height; y++)
            {
                if (!flipped.Pixels.AsSpan(y * rowBytes, rowBytes).SequenceEqual(image.Pixels.AsSpan((image.Height - 1 - y) * rowBytes, rowBytes)))
                {
                    throw new InvalidDataException($"{ours.Path} is not the flipped {_icons.Package.GetPath(sourceExport)} icon.");
                }
            }
        }

        _messages.Add($"checked: {classes} classes decode exactly, {textures} new textures read back, {_added.Count} icons match their source flipped");
        if (_markers > 0)
        {
            _messages.Add($"markers on {_markers} effect classes");
        }

        int shown = Math.Min(changed.Count, 12);
        string more = string.Empty;
        if (changed.Count > shown)
        {
            more = ", ...";
        }

        _messages.Add($"existing objects rewritten (appended): {changed.Count} ({string.Join(", ", changed.Take(shown))}{more})");
    }

    /// <summary>
    /// Texture objects for the icons over the target, laid out like the icon package's textures of the same package
    /// version: tagged properties, then Lineage 2's material block (identical in every icon except for the object's
    /// own full name), then the mips (one here).
    /// </summary>
    private static class IconTextureObject
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

            var writer = new ByteWriter();
            var tags = new TagWriter(editor, writer);
            IReadOnlyList<PropertyTag> properties = PropertyTags.Read(d, start, end, layoutPackage.GetName, out int propertiesEnd);
            foreach (PropertyTag property in properties)
            {
                if (property.Type is not (PropertyTypes.Byte or PropertyTypes.Int or PropertyTypes.Bool))
                {
                    throw new InvalidDataException($"{layoutTexture.Path}: unexpected property {property.Name} (type {property.Type}).");
                }

                // Byte/int/bool tags hold no name or object references after the name, so the rest is copied as is.
                int afterName = property.Start;
                CompactIndex.Read(d, ref afterName);
                writer.Compact(editor.Name(property.Name));
                writer.Bytes(d.AsSpan(afterName, property.End - afterName));
            }

            tags.None();

            MipLevel mip = layoutTexture.Mips[0];
            int mipTable = mip.DataOffset - CompactIndex.Encode(mip.DataSize).Length - 4 - CompactIndex.Encode(layoutTexture.Mips.Count).Length;
            byte[] oldName = Encoding.Latin1.GetBytes(layoutFullName + "\0");
            int nameAt = d.AsSpan(propertiesEnd, mipTable - propertiesEnd).IndexOf(oldName) + propertiesEnd;
            int nameLength = CompactIndex.Encode(oldName.Length).Length;
            int before = nameAt - nameLength - propertiesEnd;
            int after = mipTable - (nameAt + oldName.Length);
            int check = nameAt - nameLength;
            if (nameAt < propertiesEnd || before != MaterialBlockBeforeName || after != MaterialBlockAfterName || CompactIndex.Read(d, ref check) != oldName.Length)
            {
                throw new InvalidDataException($"{layoutTexture.Path}: material block is not laid out as expected (name at {nameAt - start}, {before} bytes before, {after} after).");
            }

            writer.Bytes(d.AsSpan(propertiesEnd, before));
            writer.Compact(fullName.Length + 1);
            writer.Bytes(Encoding.Latin1.GetBytes(fullName));
            writer.Byte(0);
            writer.Bytes(d.AsSpan(nameAt + oldName.Length, after));

            writer.Compact(1);                  // one mip
            skipAt = writer.Length;
            writer.Int32(0);                    // absolute offset of the end of the mip data, set when the package is built
            writer.Compact(mip.DataSize);
            writer.Bytes(flippedDxt1);
            skipValue = writer.Length;
            writer.Bytes(d.AsSpan(mip.DataOffset + mip.DataSize, 10)); // USize, VSize, UBits, VBits of mip 0
            return writer.ToArray();
        }
    }
}
