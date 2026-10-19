using L2Mod.Core.Packages;

namespace L2Mod.Core.Textures;

/// <summary>One mip level; <see cref="DataOffset"/> is relative to the decrypted package body.</summary>
public sealed record MipLevel(int Width, int Height, int DataOffset, int DataSize);

public sealed record TextureData(string Path, int Format, int Width, int Height, IReadOnlyList<MipLevel> Mips);

public static class TextureFormats
{
    public const int Dxt1 = 3;

    public static string GetName(int format)
    {
        switch (format)
        {
            case 0:
                return "P8";
            case 3:
                return "DXT1";
            case 5:
                return "RGBA8";
            case 7:
                return "DXT3";
            case 8:
                return "DXT5";
            default:
                return $"format {format}";
        }
    }
}

/// <summary>
/// Reads Texture exports. Properties are parsed the standard UE2 way; the mip table is located by a
/// self-validating scan, because Lineage 2 (licensee) inserts its own material data between the
/// properties and the mips. A candidate only matches when the skip offset, data size and the
/// trailing USize/VSize all agree with the texture properties.
/// </summary>
public static class TextureReader
{
    private const int TypeByte = 1;
    private const int TypeInt = 2;
    private const int TypeBool = 3;
    private const int TypeStruct = 10;
    private const int MaxMipCount = 16;

    public static TextureData Read(UnrealPackage package, ExportEntry export)
    {
        string className = package.GetClassName(export);
        if (!string.Equals(className, "Texture", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{package.GetPath(export)} is a {className}, not a Texture.");
        }

        int start = export.SerialOffset;
        int end = export.SerialOffset + export.SerialSize;
        var properties = ReadProperties(package, start, end);
        if (properties.Format != TextureFormats.Dxt1)
        {
            throw new NotSupportedException(
                $"{package.GetPath(export)} uses {TextureFormats.GetName(properties.Format)}; only DXT1 is supported.");
        }

        if (properties.Width <= 0 || properties.Height <= 0)
        {
            throw new InvalidDataException($"{package.GetPath(export)} has no USize/VSize properties.");
        }

        IReadOnlyList<MipLevel> mips = FindMips(package.Data, properties.End, end, properties.Width, properties.Height);
        return new TextureData(package.GetPath(export), properties.Format, properties.Width, properties.Height, mips);
    }

    private static TextureProperties ReadProperties(UnrealPackage package, int start, int end)
    {
        var reader = new ByteReader(package.Data, start);
        var result = new TextureProperties();
        while (reader.Position < end)
        {
            string name = package.GetName(reader.ReadCompactIndex());
            if (name == "None")
            {
                break;
            }

            byte info = reader.ReadByte();
            int type = info & 0x0F;
            int sizeCode = (info >> 4) & 0x07;
            bool arrayFlag = (info & 0x80) != 0;
            if (type == TypeStruct)
            {
                reader.ReadCompactIndex();
            }

            int size = ReadPropertySize(reader, sizeCode);
            if (arrayFlag && type != TypeBool)
            {
                SkipArrayIndex(reader);
            }

            if (type == TypeBool)
            {
                continue;
            }

            int valueStart = reader.Position;
            if (name == "Format" && type == TypeByte)
            {
                result.Format = reader.ReadByte();
            }
            else if (name == "USize" && type == TypeInt)
            {
                result.Width = reader.ReadInt32();
            }
            else if (name == "VSize" && type == TypeInt)
            {
                result.Height = reader.ReadInt32();
            }

            reader.Position = valueStart + size;
        }

        result.End = reader.Position;
        return result;
    }

    private static int ReadPropertySize(ByteReader reader, int sizeCode)
    {
        switch (sizeCode)
        {
            case 0:
                return 1;
            case 1:
                return 2;
            case 2:
                return 4;
            case 3:
                return 12;
            case 4:
                return 16;
            case 5:
                return reader.ReadByte();
            case 6:
                return reader.ReadUInt16();
            default:
                return reader.ReadInt32();
        }
    }

    private static void SkipArrayIndex(ByteReader reader)
    {
        byte first = reader.ReadByte();
        if ((first & 0x80) == 0)
        {
            return;
        }

        if ((first & 0xC0) == 0x80)
        {
            reader.Skip(1);
        }
        else
        {
            reader.Skip(3);
        }
    }

    private static IReadOnlyList<MipLevel> FindMips(byte[] data, int searchStart, int end, int width, int height)
    {
        for (int position = searchStart; position < end; position++)
        {
            List<MipLevel>? mips = TryReadMips(data, position, end, width, height);
            if (mips is not null)
            {
                return mips;
            }
        }

        throw new InvalidDataException("Mip table not found (unexpected texture layout).");
    }

    private static List<MipLevel>? TryReadMips(byte[] data, int position, int end, int width, int height)
    {
        try
        {
            var reader = new ByteReader(data, position);
            int count = reader.ReadCompactIndex();
            if (count < 1 || count > MaxMipCount)
            {
                return null;
            }

            var mips = new List<MipLevel>(count);
            int mipWidth = width;
            int mipHeight = height;
            for (int i = 0; i < count; i++)
            {
                MipLevel? mip = TryReadMip(reader, mipWidth, mipHeight, end);
                if (mip is null)
                {
                    return null;
                }

                mips.Add(mip);
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }

            return mips;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static MipLevel? TryReadMip(ByteReader reader, int width, int height, int end)
    {
        int skipOffset = reader.ReadInt32();
        int size = reader.ReadCompactIndex();
        int dataOffset = reader.Position;
        if (size != Dxt1Codec.GetDataSize(width, height))
        {
            return null;
        }

        if (skipOffset != dataOffset + size || skipOffset > end)
        {
            return null;
        }

        reader.Skip(size);
        int storedWidth = reader.ReadInt32();
        int storedHeight = reader.ReadInt32();
        reader.ReadByte();
        reader.ReadByte();
        if (storedWidth != width || storedHeight != height || reader.Position > end)
        {
            return null;
        }

        return new MipLevel(width, height, dataOffset, size);
    }

    private sealed class TextureProperties
    {
        public int Format { get; set; } = -1;

        public int Width { get; set; }

        public int Height { get; set; }

        public int End { get; set; }
    }
}
