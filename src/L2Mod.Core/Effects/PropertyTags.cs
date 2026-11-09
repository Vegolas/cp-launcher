using System.Buffers.Binary;

namespace L2Mod.Core.Effects;

/// <summary>UE2 compact index: sign in bit 7 and continuation in bit 6 of the first byte (6 value bits), then 7 value
/// bits and a continuation bit per byte.</summary>
public static class CompactIndex
{
    public static int Read(byte[] data, ref int offset)
    {
        byte first = data[offset++];
        int value = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6;
            byte next;
            do
            {
                next = data[offset++];
                value |= (next & 0x7F) << shift;
                shift += 7;
            }
            while ((next & 0x80) != 0 && shift < 32);
        }

        if ((first & 0x80) != 0)
        {
            return -value;
        }

        return value;
    }

    public static byte[] Encode(int value)
    {
        int magnitude = Math.Abs(value);
        byte sign = 0;
        if (value < 0)
        {
            sign = 0x80;
        }

        if (magnitude < 0x40)
        {
            return new[] { (byte)(sign | magnitude) };
        }

        var bytes = new List<byte> { (byte)(sign | 0x40 | (magnitude & 0x3F)) };
        magnitude >>= 6;
        while (magnitude >= 0x80)
        {
            bytes.Add((byte)(0x80 | (magnitude & 0x7F)));
            magnitude >>= 7;
        }

        bytes.Add((byte)magnitude);
        return bytes.ToArray();
    }
}

/// <summary>Property types as stored in the low 4 bits of a tag's info byte.</summary>
public static class PropertyTypes
{
    public const int Byte = 1;
    public const int Int = 2;
    public const int Bool = 3;
    public const int Float = 4;
    public const int Object = 5;
    public const int Array = 9;
    public const int Struct = 10;
    public const int String = 13;
}

/// <summary>
/// A tagged property. <see cref="Start"/> is where the tag begins, <see cref="ValueOffset"/> where its value begins;
/// <see cref="End"/> is where the next tag begins. Bools carry their value in the info byte and have size 0.
/// </summary>
public sealed record PropertyTag(string Name, int Type, string StructName, int Start, int ValueOffset, int Size)
{
    public int End
    {
        get
        {
            return ValueOffset + Size;
        }
    }
}

/// <summary>Reads UE2 tagged properties as this package version (133/40) stores them.</summary>
public static class PropertyTags
{
    /// <summary>
    /// The tags from <paramref name="start"/> up to (not including) the None that ends them; <paramref name="next"/>
    /// is the offset after None. Throws when the stream runs past <paramref name="end"/> or has no None.
    /// </summary>
    public static IReadOnlyList<PropertyTag> Read(byte[] data, int start, int end, Func<int, string> nameAt, out int next)
    {
        var tags = new List<PropertyTag>();
        int o = start;
        while (o < end)
        {
            int tagStart = o;
            string name = nameAt(CompactIndex.Read(data, ref o));
            if (name == "None")
            {
                next = o;
                return tags;
            }

            byte info = data[o++];
            int type = info & 0x0F;
            string structName = string.Empty;
            if (type == PropertyTypes.Struct)
            {
                structName = nameAt(CompactIndex.Read(data, ref o));
            }

            int size;
            switch ((info >> 4) & 7)
            {
                case 0:
                    size = 1;
                    break;
                case 1:
                    size = 2;
                    break;
                case 2:
                    size = 4;
                    break;
                case 3:
                    size = 12;
                    break;
                case 4:
                    size = 16;
                    break;
                case 5:
                    size = data[o];
                    o += 1;
                    break;
                case 6:
                    size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o));
                    o += 2;
                    break;
                default:
                    size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
                    o += 4;
                    break;
            }

            if (type == PropertyTypes.Bool)
            {
                size = 0;
            }
            else if ((info & 0x80) != 0)
            {
                if (data[o] >= 0x80)
                {
                    throw new InvalidDataException($"Array index >= 128 for {name}; not handled.");
                }

                o++;
            }

            tags.Add(new PropertyTag(name, type, structName, tagStart, o, size));
            o += size;
        }

        throw new InvalidDataException("Tagged properties run past their object without a None terminator.");
    }

    public static IReadOnlyList<PropertyTag> Read(byte[] data, int start, int end, Func<int, string> nameAt)
    {
        return Read(data, start, end, nameAt, out _);
    }
}

/// <summary>Layout of a script-less UClass object of this package version (133/40).</summary>
public static class ClassLayout
{
    /// <summary>
    /// Offset of the tagged defaults in a UClass object, checked against all 4936 classes of L2Dex's
    /// LineageSkillEffect.u and the effect classes of LineageEffect.u. Classes with script bytecode are not handled.
    /// </summary>
    public static int DefaultsOffset(byte[] data)
    {
        int o = 0;
        for (int i = 0; i < 6; i++)
        {
            CompactIndex.Read(data, ref o); // SuperField, Next, ScriptText, Children, FriendlyName, CppText
        }

        o += 8; // Line, TextPos
        int scriptSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
        if (scriptSize != 0)
        {
            throw new NotSupportedException("Classes with script bytecode are not handled.");
        }

        o += 4 + 22 + 4 + 16; // ScriptSize, UState fields, ClassFlags, ClassGuid
        int dependencies = CompactIndex.Read(data, ref o);
        for (int i = 0; i < dependencies; i++)
        {
            CompactIndex.Read(data, ref o);
            o += 8;
        }

        int packageImports = CompactIndex.Read(data, ref o);
        for (int i = 0; i < packageImports; i++)
        {
            CompactIndex.Read(data, ref o);
        }

        CompactIndex.Read(data, ref o); // ClassWithin
        CompactIndex.Read(data, ref o); // ClassConfigName
        int hideCategories = CompactIndex.Read(data, ref o);
        for (int i = 0; i < hideCategories; i++)
        {
            CompactIndex.Read(data, ref o);
        }

        return o;
    }
}
