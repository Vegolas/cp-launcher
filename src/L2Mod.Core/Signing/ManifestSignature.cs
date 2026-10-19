using System.Security.Cryptography;
using L2Mod.Core.Manifests;

namespace L2Mod.Core.Signing;

/// <summary>PEM-encoded P-256 key pair: PKCS#8 private key, SubjectPublicKeyInfo public key.</summary>
public sealed record SigningKeyPair(string PrivateKeyPem, string PublicKeyPem);

/// <summary>
/// Detached ECDSA P-256 / SHA-256 signature over the exact bytes of manifest.json (manifest.json.sig).
/// The signature is DER-encoded (RFC 3279), so it can also be checked with
/// <c>openssl dgst -sha256 -verify public.pem -signature manifest.json.sig manifest.json</c>.
/// </summary>
public static class ManifestSignature
{
    private const DSASignatureFormat Format = DSASignatureFormat.Rfc3279DerSequence;
    private const int KeySize = 256;

    public static SigningKeyPair GenerateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeyPair(key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static byte[] Sign(ReadOnlySpan<byte> manifestBytes, string privateKeyPem)
    {
        using ECDsa key = ImportKey(privateKeyPem);
        return key.SignData(manifestBytes, HashAlgorithmName.SHA256, Format);
    }

    /// <summary>False for any mismatch, including a malformed signature or the wrong key.</summary>
    public static bool Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        using ECDsa key = ImportKey(publicKeyPem);
        try
        {
            return key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, Format);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Checks the signature over the exact bytes first and only then parses the manifest.</summary>
    public static ManifestReadResult ReadVerified(byte[] manifestBytes, byte[] signature, string publicKeyPem, ManifestLimits? limits = null)
    {
        ManifestLimits effective = limits ?? ManifestLimits.Default;
        if (manifestBytes.Length > effective.MaxManifestBytes)
        {
            throw new ManifestException($"Manifest has {manifestBytes.Length} bytes; the limit is {effective.MaxManifestBytes}.");
        }

        if (!Verify(manifestBytes, signature, publicKeyPem))
        {
            throw new ManifestException("Manifest signature is not valid; the manifest was not signed by the CP leader or was changed.");
        }

        return ManifestParser.Parse(manifestBytes, effective);
    }

    private static ECDsa ImportKey(string pem)
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
