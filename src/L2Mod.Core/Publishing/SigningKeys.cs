using System.Security.Cryptography;

namespace L2Mod.Core.Publishing;

/// <summary>Helpers around the PEM keys of <see cref="Signing.ManifestSignature"/>.</summary>
public static class SigningKeys
{
    private const int KeySize = 256;

    /// <summary>The SubjectPublicKeyInfo PEM of a PKCS#8 private key.</summary>
    public static string GetPublicKeyPem(string privateKeyPem)
    {
        using ECDsa key = Import(privateKeyPem);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// SHA-256 of the DER SubjectPublicKeyInfo, lower-case hex. A private key PEM gives the fingerprint of its
    /// public half, so the two can be compared, e.g. with the key built into the launcher.
    /// </summary>
    public static string GetFingerprint(string pem)
    {
        using ECDsa key = Import(pem);
        return ContentHash.Sha256Hex(key.ExportSubjectPublicKeyInfo());
    }

    private static ECDsa Import(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != KeySize)
            {
                throw new CryptographicException($"Expected a P-256 key, got {key.KeySize} bits.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
