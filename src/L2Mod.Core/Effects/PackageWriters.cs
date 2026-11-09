using System.Buffers.Binary;
using System.Text;

namespace L2Mod.Core.Effects;

/// <summary>Little-endian writer for package objects and tables.</summary>
public sealed class ByteWriter
{
    private readonly MemoryStream _stream = new MemoryStream();

    public int Length
    {
        get
        {
            return (int)_stream.Length;
        }
    }

    public void Compact(int value)
    {
        _stream.Write(CompactIndex.Encode(value));
    }

    public void Byte(byte value)
    {
        _stream.WriteByte(value);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        _stream.Write(value);
    }

    public void Int32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void UInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void UInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void Single(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public byte[] ToArray()
    {
        return _stream.ToArray();
    }
}

/// <summary>
/// UE2 tagged properties as this package version writes them: bools carry their value in bit 7 with a size byte of
/// 0, structs name their struct after the info byte, Range/RangeVector are nested tagged structs, Vector is 12 raw
/// bytes. Names are added to the package's name table when missing.
/// </summary>
public sealed class TagWriter
{
    private readonly PackageEditor _editor;
    private readonly ByteWriter _writer;

    public TagWriter(PackageEditor editor, ByteWriter writer)
    {
        _editor = editor;
        _writer = writer;
    }

    public void Bool(string name, bool value)
    {
        _writer.Compact(_editor.Name(name));
        byte info = 0x53;
        if (value)
        {
            info |= 0x80;
        }

        _writer.Byte(info);
        _writer.Byte(0);
    }

    public void Byte(string name, byte value)
    {
        Tag(name, PropertyTypes.Byte, null, new[] { value });
    }

    public void Int(string name, int value)
    {
        var bytes = new ByteWriter();
        bytes.Int32(value);
        Tag(name, PropertyTypes.Int, null, bytes.ToArray());
    }

    public void Float(string name, float value)
    {
        var bytes = new ByteWriter();
        bytes.Single(value);
        Tag(name, PropertyTypes.Float, null, bytes.ToArray());
    }

    public void Object(string name, int reference)
    {
        Tag(name, PropertyTypes.Object, null, CompactIndex.Encode(reference));
    }

    public void ObjectArray(string name, IReadOnlyList<int> references)
    {
        var bytes = new ByteWriter();
        bytes.Compact(references.Count);
        foreach (int reference in references)
        {
            bytes.Compact(reference);
        }

        Tag(name, PropertyTypes.Array, null, bytes.ToArray());
    }

    public void Vector(string name, float x, float y, float z)
    {
        var bytes = new ByteWriter();
        bytes.Single(x);
        bytes.Single(y);
        bytes.Single(z);
        Tag(name, PropertyTypes.Struct, "Vector", bytes.ToArray());
    }

    public void Range(string name, float min, float max)
    {
        Tag(name, PropertyTypes.Struct, "Range", RangeBytes(min, max));
    }

    /// <summary>Each axis as a fixed value (Min = Max).</summary>
    public void RangeVector(string name, float x, float y, float z)
    {
        var bytes = new ByteWriter();
        var inner = new TagWriter(_editor, bytes);
        inner.Tag("X", PropertyTypes.Struct, "Range", RangeBytes(x, x));
        inner.Tag("Y", PropertyTypes.Struct, "Range", RangeBytes(y, y));
        inner.Tag("Z", PropertyTypes.Struct, "Range", RangeBytes(z, z));
        inner.None();
        Tag(name, PropertyTypes.Struct, "RangeVector", bytes.ToArray());
    }

    public void Str(string name, string value)
    {
        var bytes = new ByteWriter();
        bytes.Compact(value.Length + 1);
        bytes.Bytes(Encoding.Latin1.GetBytes(value));
        bytes.Byte(0);
        Tag(name, PropertyTypes.String, null, bytes.ToArray());
    }

    public void None()
    {
        _writer.Compact(_editor.Name("None"));
    }

    private byte[] RangeBytes(float min, float max)
    {
        var bytes = new ByteWriter();
        var inner = new TagWriter(_editor, bytes);
        inner.Float("Min", min);
        inner.Float("Max", max);
        inner.None();
        return bytes.ToArray();
    }

    private void Tag(string name, int type, string? structName, byte[] value)
    {
        int sizeCode;
        if (value.Length == 1)
        {
            sizeCode = 0;
        }
        else if (value.Length == 2)
        {
            sizeCode = 1;
        }
        else if (value.Length == 4)
        {
            sizeCode = 2;
        }
        else if (value.Length == 12)
        {
            sizeCode = 3;
        }
        else if (value.Length == 16)
        {
            sizeCode = 4;
        }
        else if (value.Length <= 255)
        {
            sizeCode = 5;
        }
        else if (value.Length <= 65535)
        {
            sizeCode = 6;
        }
        else
        {
            sizeCode = 7;
        }

        _writer.Compact(_editor.Name(name));
        _writer.Byte((byte)(type | (sizeCode << 4)));
        if (structName is not null)
        {
            _writer.Compact(_editor.Name(structName));
        }

        if (sizeCode == 5)
        {
            _writer.Byte((byte)value.Length);
        }
        else if (sizeCode == 6)
        {
            _writer.UInt16((ushort)value.Length);
        }
        else if (sizeCode == 7)
        {
            _writer.Int32(value.Length);
        }

        _writer.Bytes(value);
    }
}
