using System.Security.Cryptography;
using System.Text;
using L2Mod.Core.Manifests;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests;

public class ManifestSignatureTests
{
    private static readonly SigningKeyPair Keys = ManifestSignature.GenerateKeyPair();

    [Fact]
    public void GenerateKeyPair_ProducesPemKeys()
    {
        Assert.Contains("BEGIN PRIVATE KEY", Keys.PrivateKeyPem);
        Assert.Contains("BEGIN PUBLIC KEY", Keys.PublicKeyPem);
        Assert.DoesNotContain("PRIVATE", Keys.PublicKeyPem);
    }

    [Fact]
    public void SignedManifest_Verifies()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson);

        byte[] signature = ManifestSignature.Sign(manifest, Keys.PrivateKeyPem);

        Assert.True(ManifestSignature.Verify(manifest, signature, Keys.PublicKeyPem));
        ManifestReadResult result = ManifestSignature.ReadVerified(manifest, signature, Keys.PublicKeyPem);
        Assert.Equal(3, result.Manifest.Packs.Count);
    }

    [Fact]
    public void ChangingAnySingleByteOfTheManifestFailsVerification()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson);
        byte[] signature = ManifestSignature.Sign(manifest, Keys.PrivateKeyPem);

        foreach (int position in new[] { 0, manifest.Length / 2, manifest.Length - 1 })
        {
            byte[] tampered = (byte[])manifest.Clone();
            tampered[position] ^= 0x01;

            Assert.False(ManifestSignature.Verify(tampered, signature, Keys.PublicKeyPem));
            Assert.Throws<ManifestException>(() => ManifestSignature.ReadVerified(tampered, signature, Keys.PublicKeyPem));
        }
    }

    [Fact]
    public void ReformattedOrEditedManifestFailsVerification()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson);
        byte[] signature = ManifestSignature.Sign(manifest, Keys.PrivateKeyPem);
        byte[] edited = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson.Replace("\"enabled\": true", "\"enabled\": false"));
        byte[] reformatted = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson.Replace("\n", "\r\n"));

        Assert.False(ManifestSignature.Verify(edited, signature, Keys.PublicKeyPem));
        Assert.False(ManifestSignature.Verify(reformatted, signature, Keys.PublicKeyPem));
    }

    [Fact]
    public void SignatureFromAnotherKeyFailsVerification()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson);
        SigningKeyPair attacker = ManifestSignature.GenerateKeyPair();

        byte[] signature = ManifestSignature.Sign(manifest, attacker.PrivateKeyPem);

        Assert.False(ManifestSignature.Verify(manifest, signature, Keys.PublicKeyPem));
    }

    [Fact]
    public void DamagedOrTruncatedSignatureFailsVerification()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(ManifestParserTests.ExampleJson);
        byte[] signature = ManifestSignature.Sign(manifest, Keys.PrivateKeyPem);
        byte[] damaged = (byte[])signature.Clone();
        damaged[damaged.Length - 1] ^= 0x01;

        Assert.False(ManifestSignature.Verify(manifest, damaged, Keys.PublicKeyPem));
        Assert.False(ManifestSignature.Verify(manifest, signature.AsSpan(0, signature.Length - 1), Keys.PublicKeyPem));
        Assert.False(ManifestSignature.Verify(manifest, Array.Empty<byte>(), Keys.PublicKeyPem));
    }

    [Fact]
    public void KeysOtherThanP256AreRejected()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<CryptographicException>(() => ManifestSignature.Sign(new byte[] { 1 }, p384.ExportPkcs8PrivateKeyPem()));
    }
}
