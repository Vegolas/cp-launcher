using System.Buffers.Binary;
using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public sealed record TestTexture(string Name, RgbaImage Image, int MipCount);

/// <summary>
/// Builds a minimal UE2 texture package laid out like Lineage 2 Icon.utx: properties, an opaque
/// licensee blob, then the mip table (skip offset, compact size, data, USize, VSize, UBits, VBits).
/// All textures live in the group "skill_i".
/// </summary>
public static class TestPackageBuilder
{
    private static readonly string[] FixedNames =
    {
        "None", "Format", "USize", "VSize", "Core", "Engine", "Class", "Package",
        "Texture", "skill_i", "InternalTime", "UClamp", "bMasked",
    };

    public static byte[] BuildPlain(RgbaImage image, int mipCount)
    {
        return BuildPlain(new TestTexture("skill1235", image, mipCount));
    }

    public static byte[] BuildPlain(params TestTexture[] textures)
    {
        var names = new List<string>(FixedNames);
        foreach (TestTexture texture in textures)
        {
            names.Add(texture.Name);
        }

        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        stream.Write(new byte[64]);

        int nameOffset = (int)stream.Position;
        foreach (string name in names)
        {
            WriteCompact(writer, name.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(0u);
        }

        int importOffset = (int)stream.Position;
        WriteImport(writer, names.IndexOf("Core"), names.IndexOf("Package"), 0, names.IndexOf("Engine"));
        WriteImport(writer, names.IndexOf("Core"), names.IndexOf("Class"), -1, names.IndexOf("Texture"));
        WriteImport(writer, names.IndexOf("Core"), names.IndexOf("Class"), 0, names.IndexOf("Package"));

        var textureOffsets = new List<int>();
        var textureSizes = new List<int>();
        foreach (TestTexture texture in textures)
        {
            int textureOffset = (int)stream.Position;
            byte[] textureData = BuildTextureData(names, texture.Image, texture.MipCount, textureOffset);
            stream.Write(textureData);
            textureOffsets.Add(textureOffset);
            textureSizes.Add(textureData.Length);
        }

        int exportOffset = (int)stream.Position;
        WriteExport(writer, -3, 0, names.IndexOf("skill_i"), 0, 0);
        for (int i = 0; i < textures.Length; i++)
        {
            WriteExport(writer, -2, 1, names.IndexOf(textures[i].Name), textureSizes[i], textureOffsets[i]);
        }

        byte[] package = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(package, UnrealPackage.Signature);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(4), 123);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(6), 40);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), names.Count);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(16), nameOffset);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(20), textures.Length + 1);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(24), exportOffset);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(28), 3);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(32), importOffset);
        return package;
    }

    public static byte[] Encrypt(byte[] body, string version, byte key)
    {
        byte[] header = Encoding.Unicode.GetBytes("Lineage2Ver" + version);
        byte[] output = new byte[header.Length + body.Length];
        header.CopyTo(output, 0);
        for (int i = 0; i < body.Length; i++)
        {
            output[header.Length + i] = (byte)(body[i] ^ key);
        }

        return output;
    }

    public static RgbaImage SolidImage(int width, int height, byte red, byte green, byte blue)
    {
        var image = new RgbaImage(width, height);
        image.FillRectangle(new PixelRect(0, 0, width, height), red, green, blue);
        return image;
    }

    /// <summary>Deterministic noisy image so every DXT1 block differs.</summary>
    public static RgbaImage NoiseImage(int width, int height, int seed)
    {
        var image = new RgbaImage(width, height);
        var random = new Random(seed);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            }
        }

        return image;
    }

    private static byte[] BuildTextureData(List<string> names, RgbaImage image, int mipCount, int absoluteStart)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        WriteCompact(writer, names.IndexOf("InternalTime"));
        writer.Write((byte)0x22);
        writer.Write(0x18398977);
        WriteCompact(writer, names.IndexOf("bMasked"));
        writer.Write((byte)0x83);
        WriteCompact(writer, names.IndexOf("Format"));
        writer.Write((byte)0x01);
        writer.Write((byte)TextureFormats.Dxt1);
        WriteCompact(writer, names.IndexOf("USize"));
        writer.Write((byte)0x22);
        writer.Write(image.Width);
        WriteCompact(writer, names.IndexOf("VSize"));
        writer.Write((byte)0x22);
        writer.Write(image.Height);
        WriteCompact(writer, names.IndexOf("UClamp"));
        writer.Write((byte)0x22);
        writer.Write(image.Width);
        WriteCompact(writer, names.IndexOf("None"));

        writer.Write(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x05, 0x08, 0x20, 0x00, 0x01, 0x17, 0x49, 0x63, 0x6F, 0x6E, 0x00, 0x32, 0x03 });

        WriteCompact(writer, mipCount);
        int width = image.Width;
        int height = image.Height;
        RgbaImage mipImage = image;
        for (int mip = 0; mip < mipCount; mip++)
        {
            byte[] encoded = Dxt1Codec.Encode(mipImage);
            int skipPosition = (int)stream.Position;
            writer.Write(0);
            WriteCompact(writer, encoded.Length);
            writer.Write(encoded);
            int end = absoluteStart + (int)stream.Position;
            writer.Write(width);
            writer.Write(height);
            writer.Write((byte)Math.Log2(width));
            writer.Write((byte)Math.Log2(height));

            long current = stream.Position;
            stream.Position = skipPosition;
            writer.Write(end);
            stream.Position = current;

            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            mipImage = image.DownscaleBox(image.Width / width);
        }

        return stream.ToArray();
    }

    private static void WriteImport(BinaryWriter writer, int classPackage, int className, int outer, int objectName)
    {
        WriteCompact(writer, classPackage);
        WriteCompact(writer, className);
        writer.Write(outer);
        WriteCompact(writer, objectName);
    }

    private static void WriteExport(BinaryWriter writer, int classIndex, int outer, int objectName, int size, int offset)
    {
        WriteCompact(writer, classIndex);
        WriteCompact(writer, 0);
        writer.Write(outer);
        WriteCompact(writer, objectName);
        writer.Write(0x000E0004u);
        WriteCompact(writer, size);
        if (size > 0)
        {
            WriteCompact(writer, offset);
        }
    }

    private static void WriteCompact(BinaryWriter writer, int value)
    {
        int magnitude = Math.Abs(value);
        byte first = (byte)(magnitude & 0x3F);
        if (value < 0)
        {
            first |= 0x80;
        }

        magnitude >>= 6;
        if (magnitude > 0)
        {
            first |= 0x40;
        }

        writer.Write(first);
        while (magnitude > 0)
        {
            byte next = (byte)(magnitude & 0x7F);
            magnitude >>= 7;
            if (magnitude > 0)
            {
                next |= 0x80;
            }

            writer.Write(next);
        }
    }
}
