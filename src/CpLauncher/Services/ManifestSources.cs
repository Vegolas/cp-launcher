using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;

namespace CpLauncher.Services;

/// <summary>
/// Where manifest.json, manifest.json.sig and assets/ come from. Nothing read from a source is trusted until the
/// manifest signature (and, for assets, the hash from the signed manifest) has been checked.
/// </summary>
public interface IManifestSource
{
    /// <summary>Shown in the status ("https://…" or the folder path).</summary>
    string Description { get; }

    /// <summary>
    /// Reads one distribution file. Throws <see cref="SourceUnavailableException"/> when the source cannot be
    /// reached or the file is not there, and <see cref="ManifestException"/> when it is larger than <paramref name="maxBytes"/>.
    /// </summary>
    Task<byte[]> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>The source cannot be reached (offline, DNS, timeout, missing file); the launcher falls back to its cache.</summary>
public sealed class SourceUnavailableException : Exception
{
    public SourceUnavailableException(string message)
        : base(message)
    {
    }

    public SourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Only the files of the distribution layout may be requested.</summary>
public static partial class DistributionPaths
{
    public static bool IsAllowed(string relativePath)
    {
        return relativePath == ManifestFiles.Manifest
            || relativePath == ManifestFiles.Signature
            || AssetPathPattern().IsMatch(relativePath);
    }

    public static void Require(string relativePath)
    {
        if (!IsAllowed(relativePath))
        {
            throw new ArgumentException($"'{relativePath}' is not a distribution file.");
        }
    }

    [GeneratedRegex("^assets/[0-9a-f]{64}\\.png\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathPattern();
}

/// <summary>Static hosting (Cloudflare R2 or any web server): GET &lt;base&gt;/manifest.json and so on.</summary>
public sealed class HttpManifestSource : IManifestSource
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public HttpManifestSource(HttpClient client, Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"{baseUri} is not an http(s) address.");
        }

        _client = client;
        _baseUri = EnsureTrailingSlash(baseUri);
    }

    public string Description
    {
        get
        {
            return _baseUri.ToString();
        }
    }

    /// <summary>The folder of manifest.json, with a trailing slash.</summary>
    public Uri BaseUri
    {
        get
        {
            return _baseUri;
        }
    }

    public async Task<byte[]> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken)
    {
        DistributionPaths.Require(relativePath);
        var uri = new Uri(_baseUri, relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!relativePath.StartsWith("assets/", StringComparison.Ordinal))
        {
            // Assets are content-addressed; the manifest and its signature must not come from a stale cache.
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        }

        try
        {
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SourceUnavailableException($"{uri} nie istnieje (404).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceUnavailableException($"{uri} zwrócił błąd {(int)response.StatusCode}.");
            }

            long? length = response.Content.Headers.ContentLength;
            if (length > maxBytes)
            {
                throw new ManifestException($"{relativePath} has {length} bytes; the limit is {maxBytes}.");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadLimitedAsync(stream, relativePath, maxBytes, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new SourceUnavailableException($"Brak połączenia z {_baseUri.Host}: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceUnavailableException($"{_baseUri.Host} nie odpowiada (przekroczono czas).", exception);
        }
        catch (IOException exception)
        {
            throw new SourceUnavailableException($"Przerwane pobieranie z {_baseUri.Host}: {exception.Message}", exception);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, string relativePath, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new ManifestException($"{relativePath} is larger than the limit of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        string text = uri.ToString();
        if (text.EndsWith('/'))
        {
            return uri;
        }

        return new Uri(text + "/");
    }
}

/// <summary>A local folder laid out like the hosting (development, or a leader's dist\ folder).</summary>
public sealed class FolderManifestSource : IManifestSource
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;

    public FolderManifestSource(IFileSystem fileSystem, string root)
    {
        _fileSystem = fileSystem;
        _root = root;
    }

    public string Description
    {
        get
        {
            return _root;
        }
    }

    public Task<byte[]> ReadAsync(string relativePath, int maxBytes, CancellationToken cancellationToken)
    {
        DistributionPaths.Require(relativePath);
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes;
        try
        {
            if (!_fileSystem.FileExists(path))
            {
                throw new SourceUnavailableException($"Nie znaleziono {path}.");
            }

            bytes = _fileSystem.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SourceUnavailableException($"Nie można odczytać {path}: {exception.Message}", exception);
        }

        if (bytes.Length > maxBytes)
        {
            throw new ManifestException($"{relativePath} has {bytes.Length} bytes; the limit is {maxBytes}.");
        }

        return Task.FromResult(bytes);
    }
}

/// <summary>Turns the configured source text into a source: http(s) URL, file:// URL or an absolute folder path.</summary>
public sealed class ManifestSourceFactory
{
    private readonly IFileSystem _fileSystem;
    private readonly HttpClient _httpClient;

    public ManifestSourceFactory(IFileSystem fileSystem, HttpClient httpClient)
    {
        _fileSystem = fileSystem;
        _httpClient = httpClient;
    }

    /// <summary>Null when nothing is configured; <see cref="FormatException"/> (Polish message) when the text is not usable.</summary>
    public IManifestSource? Create(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string text = configured.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new FormatException($"„{text}” nie jest poprawnym adresem przepisów.");
            }

            return new HttpManifestSource(_httpClient, uri);
        }

        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile)
            {
                throw new FormatException($"„{text}” nie jest poprawnym adresem folderu.");
            }

            return new FolderManifestSource(_fileSystem, fileUri.LocalPath);
        }

        if (Path.IsPathFullyQualified(text))
        {
            return new FolderManifestSource(_fileSystem, Path.GetFullPath(text));
        }

        throw new FormatException($"„{text}” nie jest adresem http(s) ani pełną ścieżką folderu.");
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CpLauncher", LauncherBuild.CurrentVersion.ToString()));
        return client;
    }
}
