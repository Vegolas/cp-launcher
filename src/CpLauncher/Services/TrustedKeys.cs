using System.Security.Cryptography;

namespace CpLauncher.Services;

/// <summary>A public key manifests may be signed with.</summary>
public sealed record TrustedKey(string Name, string PublicKeyPem, bool IsDevelopment);

public static class TrustedKeys
{
    private const string ResourceName = "CpLauncher.Keys.manifest-public.pem";

    /// <summary>
    /// The leader's public key, embedded in the exe.
    /// TODO(phase 2): Keys\manifest-public.pem is a placeholder whose private half was discarded, so nothing
    /// verifies against it; replace it with the public key from the leader tool's keygen before the first release.
    /// </summary>
    public static TrustedKey Embedded
    {
        get
        {
            using Stream? stream = typeof(TrustedKeys).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                throw new InvalidOperationException($"Resource {ResourceName} is missing from the launcher.");
            }

            using var reader = new StreamReader(stream);
            return new TrustedKey("CP", reader.ReadToEnd(), false);
        }
    }

    /// <summary>A key given with --dev-public-key; manifests verified with it are flagged in the UI.</summary>
    public static TrustedKey Development(string pem, string origin)
    {
        if (!IsValidPublicKey(pem))
        {
            throw new FormatException($"{origin} nie zawiera klucza publicznego P-256.");
        }

        return new TrustedKey($"dev: {origin}", pem, true);
    }

    public static bool IsValidPublicKey(string pem)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.KeySize == 256;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
