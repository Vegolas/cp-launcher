using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Signing;

namespace CpLauncher.Services;

public enum ManifestOrigin
{
    /// <summary>Downloaded and verified just now.</summary>
    Source,

    /// <summary>The last verified manifest from the cache (offline, or the source served something unusable).</summary>
    Cache,
}

/// <summary>A verified manifest. <see cref="Notes"/> are the parser's notes (skipped packs and presets).</summary>
public sealed record LoadedManifest(Manifest Manifest, IReadOnlyList<ManifestNote> Notes, ManifestOrigin Origin, TrustedKey Key)
{
    /// <summary>The English texts of <see cref="Notes"/>.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            return Notes.Select(note => note.Message).ToList();
        }
    }
}

/// <summary>
/// <see cref="Manifest"/> is null when neither the source nor the cache gave a usable manifest.
/// <see cref="Problem"/> (Polish) says why the source was not used, when it was not.
/// </summary>
public sealed record ManifestLoadResult(LoadedManifest? Manifest, string? Problem);

/// <summary>
/// Downloads manifest.json + manifest.json.sig, verifies the signature against the trusted keys through Core's
/// <see cref="ManifestSignature.ReadVerified"/>, and keeps the last verified pair for offline use. A pair that
/// fails verification never replaces the cache, and the cache is verified again every time it is read.
/// </summary>
public sealed class ManifestService
{
    private readonly IFileSystem _fileSystem;
    private readonly LauncherPaths _paths;
    private readonly IReadOnlyList<TrustedKey> _keys;

    public ManifestService(IFileSystem fileSystem, LauncherPaths paths, IReadOnlyList<TrustedKey> keys)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one trusted key is needed.", nameof(keys));
        }

        _fileSystem = fileSystem;
        _paths = paths;
        _keys = keys;
    }

    public async Task<ManifestLoadResult> LoadAsync(IManifestSource? source, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return FromCache("Nie ustawiono źródła przepisów (Ustawienia → Źródło przepisów).");
        }

        byte[] manifestBytes;
        byte[] signature;
        try
        {
            var limits = ManifestLimits.Default;
            manifestBytes = await source.ReadAsync(ManifestFiles.Manifest, limits.MaxManifestBytes, cancellationToken);
            signature = await source.ReadAsync(ManifestFiles.Signature, ManifestFiles.MaxSignatureBytes, cancellationToken);
        }
        catch (SourceUnavailableException exception)
        {
            return FromCache($"Brak dostępu do źródła przepisów: {exception.Message}");
        }
        catch (ManifestException exception)
        {
            return FromCache($"Źródło przepisów zwróciło niepoprawne dane ({exception.Message}).");
        }

        LoadedManifest verified;
        try
        {
            verified = Verify(manifestBytes, signature, ManifestOrigin.Source);
        }
        catch (ManifestException exception)
        {
            return FromCache(exception.Message);
        }

        try
        {
            VerifiedWrite.Write(_fileSystem, _paths.CachedSignaturePath, signature);
            VerifiedWrite.Write(_fileSystem, _paths.CachedManifestPath, manifestBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestLoadResult(verified, $"Nie udało się zapisać kopii manifestu do pracy offline: {exception.Message}");
        }

        return new ManifestLoadResult(verified, null);
    }

    /// <summary>The cached pair, verified again; null when there is none or it does not verify.</summary>
    public LoadedManifest? LoadCached()
    {
        if (!_fileSystem.FileExists(_paths.CachedManifestPath) || !_fileSystem.FileExists(_paths.CachedSignaturePath))
        {
            return null;
        }

        try
        {
            return Verify(_fileSystem.ReadAllBytes(_paths.CachedManifestPath), _fileSystem.ReadAllBytes(_paths.CachedSignaturePath), ManifestOrigin.Cache);
        }
        catch (Exception exception) when (exception is ManifestException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private LoadedManifest Verify(byte[] manifestBytes, byte[] signature, ManifestOrigin origin)
    {
        foreach (TrustedKey key in _keys)
        {
            if (!ManifestSignature.Verify(manifestBytes, signature, key.PublicKeyPem))
            {
                continue;
            }

            ManifestReadResult result;
            try
            {
                result = ManifestSignature.ReadVerified(manifestBytes, signature, key.PublicKeyPem);
            }
            catch (ManifestException exception)
            {
                throw new ManifestException($"Manifest ma poprawny podpis, ale nie da się go odczytać: {exception.Message}", exception);
            }

            return new LoadedManifest(result.Manifest, result.Notes, origin, key);
        }

        throw new ManifestException("Manifest ma nieprawidłowy podpis – nie pochodzi od lidera CP albo został zmieniony. Używam ostatniego sprawdzonego.");
    }

    private ManifestLoadResult FromCache(string problem)
    {
        LoadedManifest? cached = LoadCached();
        if (cached is null)
        {
            return new ManifestLoadResult(null, problem + " Brak zapisanej kopii manifestu.");
        }

        return new ManifestLoadResult(cached, problem);
    }
}
