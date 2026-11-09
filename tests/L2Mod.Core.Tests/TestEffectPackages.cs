using System.Buffers.Binary;
using System.Text;
using L2Mod.Core.Effects;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Tests;

/// <summary>
/// Synthetic system_season effect packages laid out like L2Dex's (package version 123 like the test Icon.utx):
/// <list type="bullet">
/// <item>LineageSkillEffect.u: per skill a script-less class l2_&lt;id&gt;_skill (SkillID default, ShotAction with one
/// L2EffectEmitter0 subobject) playing its own effect class fx_&lt;id&gt;;</item>
/// <item>LineageEffect.u (Ver111): the click arrow class GstarFX_arrow_click with a SpriteEmitter0 drawing the blue
/// glow Particles.FX_M_T4111 and a mesh emitter with the green Particles.fx_m_t8254 material.</item>
/// </list>
/// </summary>
public static class TestEffectPackages
{
    public const ushort Version = 123;

    /// <summary>Skills with a class of their own in <see cref="BuildSkillEffects"/>.</summary>
    public static readonly int[] Skills = { 1235, 1175, 1230, 1220, 1071, 1048 };

    /// <summary>An Icon.utx (Ver121) whose skill icons have the client's material block, for icons over the target.</summary>
    public static byte[] BuildIconUtx()
    {
        var textures = new List<TestTexture>();
        foreach (int skill in Skills.Append(1323))
        {
            textures.Add(new TestTexture($"skill{skill:D4}", TestPackageBuilder.NoiseImage(32, 32, skill), 1));
        }

        return TestPackageBuilder.Encrypt(TestPackageBuilder.BuildPlain("Icon", textures.ToArray()), "121", TestIcons.IconKey);
    }

    public static byte[] BuildSkillEffects()
    {
        return BuildSkillEffects(Skills);
    }

    public static byte[] BuildSkillEffects(IReadOnlyList<int> skills)
    {
        var builder = new Builder();
        int engine = builder.Import("Core", "Package", 0, "Engine");
        builder.Import("Core", "Package", 0, "Core");
        int effectEmitter = builder.Import("Core", "Class", engine, "L2EffectEmitter");
        int skillEffect = builder.Import("Core", "Class", engine, "L2SkillEffect");
        int emitter = builder.Import("Core", "Class", engine, "Emitter");
        foreach (int skill in skills)
        {
            int effect = builder.Export(0, emitter, 0, $"fx_{skill}");
            builder.SetData(effect, ClassData(builder, effect, emitter, new byte[0]));
            int skillClass = builder.Export(0, skillEffect, 0, EffectKinds.SkillClassName(skill));
            int subobject = builder.Export(effectEmitter, 0, skillClass, "L2EffectEmitter0");
            var tags = new MemoryStream();
            Tag(tags, builder.Name("EffectClass"), 0x05, CompactIndex.Encode(effect));
            tags.Write(CompactIndex.Encode(builder.Name("None")));
            builder.SetData(subobject, tags.ToArray());

            var defaults = new MemoryStream();
            Tag(defaults, builder.Name("SkillID"), 0x22, Int32Bytes(skill));
            var array = new MemoryStream();
            array.Write(CompactIndex.Encode(1));
            array.Write(CompactIndex.Encode(subobject));
            Tag(defaults, builder.Name("ShotAction"), 0x59, Prefixed(array.ToArray()));
            builder.SetData(skillClass, ClassData(builder, skillClass, skillEffect, defaults.ToArray()));
        }

        return builder.Build();
    }

    public static byte[] BuildEffects()
    {
        var builder = new Builder();
        int engine = builder.Import("Core", "Package", 0, "Engine");
        int particles = builder.Import("Core", "Package", 0, "Particles");
        int particles2 = builder.Import("Core", "Package", 0, "Particles2");
        int aura = builder.Import("Core", "Package", 0, "Aura");
        int blue = builder.Import("Engine", "Texture", particles, "FX_M_T4111");
        builder.Import("Engine", "Texture", particles2, "fx_m_t_3037");
        int green = builder.Import("Engine", "Texture", particles, "fx_m_t8254");
        builder.Import("Engine", "Texture", aura, "aura001_1");
        int emitterClass = builder.Import("Core", "Class", engine, "Emitter");
        int spriteEmitter = builder.Import("Core", "Class", engine, "SpriteEmitter");
        int meshEmitter = builder.Import("Core", "Class", engine, "MeshEmitter");

        int arrow = builder.Export(0, emitterClass, 0, "GstarFX_arrow_click");
        builder.SetData(arrow, ClassData(builder, arrow, emitterClass, new byte[0]));
        int sprite = builder.Export(spriteEmitter, 0, arrow, "SpriteEmitter0");
        var spriteTags = new MemoryStream();
        Tag(spriteTags, builder.Name("Texture"), 0x05, CompactIndex.Encode(blue));
        spriteTags.Write(CompactIndex.Encode(builder.Name("None")));
        builder.SetData(sprite, spriteTags.ToArray());
        int mesh = builder.Export(meshEmitter, 0, arrow, "MeshEmitter0");
        var meshTags = new MemoryStream();
        var materials = new MemoryStream();
        materials.Write(CompactIndex.Encode(1));
        materials.Write(CompactIndex.Encode(green));
        Tag(meshTags, builder.Name("CustomMaterials"), 0x59, Prefixed(materials.ToArray()));
        meshTags.Write(CompactIndex.Encode(builder.Name("None")));
        builder.SetData(mesh, meshTags.ToArray());

        // Another class's emitter with the same glow stays blue.
        int other = builder.Export(0, emitterClass, 0, "GstarFX_other");
        builder.SetData(other, ClassData(builder, other, emitterClass, new byte[0]));
        int otherSprite = builder.Export(spriteEmitter, 0, other, "SpriteEmitter0");
        builder.SetData(otherSprite, spriteTags.ToArray());
        return TestPackageBuilder.Encrypt(builder.Build(), "111", 0xAC);
    }

    /// <summary>A script-less UClass of this package version followed by the given defaults and None.</summary>
    private static byte[] ClassData(Builder builder, int classRef, int superClass, byte[] defaults)
    {
        var data = new MemoryStream();
        data.Write(CompactIndex.Encode(superClass));
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(builder.Name(builder.ExportName(classRef))));
        data.Write(CompactIndex.Encode(0));
        data.Write(Int32Bytes(-1));
        data.Write(Int32Bytes(-1));
        data.Write(Int32Bytes(0));
        data.Write(new byte[22]);
        data.Write(Int32Bytes(0x12));
        data.Write(new byte[16]);
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(0));
        data.Write(CompactIndex.Encode(builder.Name("System")));
        data.Write(CompactIndex.Encode(0));
        data.Write(defaults);
        data.Write(CompactIndex.Encode(builder.Name("None")));
        return data.ToArray();
    }

    private static void Tag(MemoryStream stream, int name, byte info, byte[] value)
    {
        stream.Write(CompactIndex.Encode(name));
        stream.WriteByte(info);
        stream.Write(value);
    }

    /// <summary>A value with a one-byte size (size code 5).</summary>
    private static byte[] Prefixed(byte[] value)
    {
        var bytes = new byte[value.Length + 1];
        bytes[0] = (byte)value.Length;
        value.CopyTo(bytes, 1);
        return bytes;
    }

    private static byte[] Int32Bytes(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private sealed class Builder
    {
        private readonly List<string> _names = new List<string> { "None" };
        private readonly List<(int ClassPackage, int ClassName, int Outer, int Name)> _imports = new List<(int ClassPackage, int ClassName, int Outer, int Name)>();
        private readonly List<(int Class, int Super, int Outer, int Name, byte[] Data)> _exports = new List<(int Class, int Super, int Outer, int Name, byte[] Data)>();

        public int Name(string name)
        {
            int index = _names.IndexOf(name);
            if (index >= 0)
            {
                return index;
            }

            _names.Add(name);
            return _names.Count - 1;
        }

        public string ExportName(int exportRef)
        {
            return _names[_exports[exportRef - 1].Name];
        }

        public int Import(string classPackage, string className, int outer, string name)
        {
            _imports.Add((Name(classPackage), Name(className), outer, Name(name)));
            return -_imports.Count;
        }

        public int Export(int classRef, int superRef, int outer, string name)
        {
            _exports.Add((classRef, superRef, outer, Name(name), Array.Empty<byte>()));
            return _exports.Count;
        }

        public void SetData(int exportRef, byte[] data)
        {
            var row = _exports[exportRef - 1];
            _exports[exportRef - 1] = (row.Class, row.Super, row.Outer, row.Name, data);
        }

        public byte[] Build()
        {
            var stream = new MemoryStream();
            stream.Write(new byte[64]);
            int nameOffset = (int)stream.Length;
            foreach (string name in _names)
            {
                stream.Write(CompactIndex.Encode(name.Length + 1));
                stream.Write(Encoding.ASCII.GetBytes(name));
                stream.WriteByte(0);
                stream.Write(Int32Bytes(0x00070010));
            }

            int importOffset = (int)stream.Length;
            foreach ((int classPackage, int className, int outer, int name) in _imports)
            {
                stream.Write(CompactIndex.Encode(classPackage));
                stream.Write(CompactIndex.Encode(className));
                stream.Write(Int32Bytes(outer));
                stream.Write(CompactIndex.Encode(name));
            }

            var offsets = new List<int>();
            foreach (var export in _exports)
            {
                offsets.Add((int)stream.Length);
                stream.Write(export.Data);
            }

            int exportOffset = (int)stream.Length;
            for (int i = 0; i < _exports.Count; i++)
            {
                var export = _exports[i];
                stream.Write(CompactIndex.Encode(export.Class));
                stream.Write(CompactIndex.Encode(export.Super));
                stream.Write(Int32Bytes(export.Outer));
                stream.Write(CompactIndex.Encode(export.Name));
                stream.Write(Int32Bytes(0x000F0004));
                stream.Write(CompactIndex.Encode(export.Data.Length));
                if (export.Data.Length > 0)
                {
                    stream.Write(CompactIndex.Encode(offsets[i]));
                }
            }

            byte[] package = stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(package, UnrealPackage.Signature);
            BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(6), 40);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), _names.Count);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(16), nameOffset);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(20), _exports.Count);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(24), exportOffset);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(28), _imports.Count);
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(32), importOffset);
            return package;
        }
    }
}
