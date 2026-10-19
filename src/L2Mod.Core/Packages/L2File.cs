using System.Buffers.Binary;
using System.Text;

namespace L2Mod.Core.Packages;

public enum L2Encryption
{
    None,
    Ver111,
    Ver121,
}

/// <summary>
/// A Lineage 2 client package as stored on disk. Handles the NCSoft XOR schemes (Lineage2Ver111/121)
/// and plain Unreal packages. RSA schemes (Ver41x) and Active Anticheat encryption are deliberately not supported.
/// </summary>
public sealed class L2File
{
    public const int HeaderSize = 28;

    private const string HeaderPrefix = "Lineage2Ver";
    private const string ActiveAnticheatPrefix = "ActiveAnticheatCrypt";
    private const byte Ver111Key = 0xAC;
    private const byte FirstSignatureByte = 0xC1;

    private L2File(byte[] rawBytes, L2Encryption encryption, byte key, byte[] body)
    {
        RawBytes = rawBytes;
        Encryption = encryption;
        Key = key;
        Body = body;
    }

    /// <summary>File bytes exactly as stored on disk.</summary>
    public byte[] RawBytes { get; }

    public L2Encryption Encryption { get; }

    /// <summary>XOR key (0 when the file is not encrypted).</summary>
    public byte Key { get; }

    /// <summary>Decrypted Unreal package bytes.</summary>
    public byte[] Body { get; }

    public static L2File Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        return FromBytes(raw);
    }

    public static L2File FromBytes(byte[] raw)
    {
        if (raw.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == UnrealPackage.Signature)
        {
            return new L2File(raw, L2Encryption.None, 0, raw);
        }

        if (StartsWithAscii(raw, ActiveAnticheatPrefix))
        {
            throw new NotSupportedException("File is encrypted by Active Anticheat. This tool does not handle it.");
        }

        string header = ReadUnicodeHeader(raw);
        if (!header.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unknown file format: neither an Unreal package nor a Lineage2Ver file.");
        }

        string version = header.Substring(HeaderPrefix.Length);
        byte key;
        L2Encryption encryption;
        if (version == "111")
        {
            key = Ver111Key;
            encryption = L2Encryption.Ver111;
        }
        else if (version == "121")
        {
            key = (byte)(raw[HeaderSize] ^ FirstSignatureByte);
            encryption = L2Encryption.Ver121;
        }
        else
        {
            throw new NotSupportedException($"Lineage2Ver{version} is not supported (only 111 and 121).");
        }

        byte[] body = new byte[raw.Length - HeaderSize];
        for (int i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(raw[HeaderSize + i] ^ key);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != UnrealPackage.Signature)
        {
            throw new InvalidDataException($"Decrypted Lineage2Ver{version} data is not an Unreal package.");
        }

        return new L2File(raw, encryption, key, body);
    }

    /// <summary>
    /// Ver121 key as the client derives it: sum of the lower-cased file name characters, low byte.
    /// The client uses the name it loads the file under, so a patched file must keep its original name.
    /// </summary>
    public static byte DeriveVer121Key(string fileName)
    {
        int sum = 0;
        foreach (char c in fileName.ToLowerInvariant())
        {
            sum += c;
        }

        return (byte)(sum & 0xFF);
    }

    /// <summary>
    /// Returns a copy of the on-disk bytes with every patch written at its package body offset, encrypted
    /// the same way as the original. Patches must not change sizes; every other byte stays identical.
    /// </summary>
    public byte[] BuildPatchedFile(IReadOnlyList<BodyPatch> patches)
    {
        byte[] output = (byte[])RawBytes.Clone();
        foreach (BodyPatch patch in patches)
        {
            if (patch.BodyOffset < 0 || patch.BodyOffset + patch.Bytes.Length > Body.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(patches), "Patch range is outside the package.");
            }

            int fileOffset = ToFileOffset(patch.BodyOffset);
            for (int i = 0; i < patch.Bytes.Length; i++)
            {
                output[fileOffset + i] = (byte)(patch.Bytes[i] ^ Key);
            }
        }

        return output;
    }

    public int ToFileOffset(int bodyOffset)
    {
        if (Encryption == L2Encryption.None)
        {
            return bodyOffset;
        }

        return HeaderSize + bodyOffset;
    }

    private static string ReadUnicodeHeader(byte[] raw)
    {
        if (raw.Length <= HeaderSize)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(raw, 0, HeaderSize);
    }

    private static bool StartsWithAscii(byte[] raw, string prefix)
    {
        if (raw.Length < prefix.Length)
        {
            return false;
        }

        return Encoding.ASCII.GetString(raw, 0, prefix.Length) == prefix;
    }
}
