using System.Security.Cryptography;

namespace L2Mod.Core;

/// <summary>SHA-256 as lower-case hex, the form used in manifests, state and backup names.</summary>
public static class ContentHash
{
    public const int Sha256HexLength = 64;

    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Matches(string expectedHex, ReadOnlySpan<byte> data)
    {
        return string.Equals(expectedHex, Sha256Hex(data), StringComparison.OrdinalIgnoreCase);
    }
}
