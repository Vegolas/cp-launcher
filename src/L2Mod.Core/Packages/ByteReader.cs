using System.Buffers.Binary;

namespace L2Mod.Core.Packages;

/// <summary>
/// Little-endian reader over a byte array with Unreal Engine 2 compact index support.
/// </summary>
public sealed class ByteReader
{
    private readonly byte[] _data;

    public ByteReader(byte[] data, int position)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        byte value = _data[Position];
        Position += 1;
        return value;
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        EnsureAvailable(count);
        ReadOnlySpan<byte> span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count)
    {
        EnsureAvailable(count);
        Position += count;
    }

    /// <summary>
    /// Reads an UE2 compact index: first byte holds sign (bit 7), continuation (bit 6) and 6 value bits,
    /// following bytes hold continuation (bit 7) and 7 value bits.
    /// </summary>
    public int ReadCompactIndex()
    {
        byte first = ReadByte();
        bool negative = (first & 0x80) != 0;
        int value = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6;
            for (int i = 0; i < 4; i++)
            {
                byte next = ReadByte();
                value |= (next & 0x7F) << shift;
                shift += 7;
                if ((next & 0x80) == 0)
                {
                    break;
                }
            }
        }

        if (negative)
        {
            value = -value;
        }

        return value;
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || Position < 0 || Position + count > _data.Length)
        {
            throw new InvalidDataException($"Unexpected end of data at offset {Position} (needed {count} bytes).");
        }
    }
}
