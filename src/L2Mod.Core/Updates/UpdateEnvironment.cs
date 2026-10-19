using System.Diagnostics;

namespace L2Mod.Core.Updates;

/// <summary>The file operations the updater needs; faked in tests.</summary>
public interface IUpdateFileSystem
{
    bool FileExists(string path);

    /// <summary>Creates a file for writing; fails when it already exists. Nobody else may open it meanwhile.</summary>
    Stream CreateNew(string path);

    Stream OpenRead(string path);

    /// <summary>Renames a file; never replaces an existing destination. Works on a running exe.</summary>
    void Move(string source, string destination);

    /// <summary>Deletes a file; no-op when it is missing. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> while the file is in use (a running exe).</summary>
    void Delete(string path);

    /// <summary>Some process has the file open, or runs it: it cannot be opened for exclusive writing.
    /// False for a missing file.</summary>
    bool IsInUse(string path);
}

public sealed class PhysicalUpdateFileSystem : IUpdateFileSystem
{
    private const int BufferSize = 81920;

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public Stream CreateNew(string path)
    {
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
    }

    public Stream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Move(string source, string destination)
    {
        File.Move(source, destination, false);
    }

    public void Delete(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }

        // A read-only exe (copied from a read-only medium) can be renamed but not deleted.
        if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
        }

        info.Delete();
    }

    public bool IsInUse(string path)
    {
        try
        {
            // Opening for writing changes nothing, but fails while the exe runs (its image section is mapped).
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return false;
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only attribute or ACL: says nothing about other processes; the rename that follows will tell.
            return false;
        }
    }
}

/// <summary>An open response body. Disposing it releases the underlying connection.</summary>
public sealed class DownloadResponse : IDisposable
{
    private readonly IDisposable? _owner;

    public DownloadResponse(Stream content, long? contentLength, IDisposable? owner = null)
    {
        Content = content;
        ContentLength = contentLength;
        _owner = owner;
    }

    public Stream Content { get; }

    /// <summary>The announced length, when the server sent one. Not trusted: the body is still counted.</summary>
    public long? ContentLength { get; }

    public void Dispose()
    {
        Content.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>Opens a URL for reading; faked in tests.</summary>
public interface IDownloadClient
{
    /// <summary>Returns the body of a successful response; throws <see cref="HttpRequestException"/> otherwise.</summary>
    Task<DownloadResponse> OpenAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDownloadClient"/> over an <see cref="HttpClient"/>. Returns after the headers, so the body is
/// streamed; <see cref="HttpClient.Timeout"/> covers only that part, the updater watches the body for stalls.
/// </summary>
public sealed class HttpDownloadClient : IDownloadClient
{
    private readonly HttpClient _httpClient;

    public HttpDownloadClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<DownloadResponse> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            }

            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DownloadResponse(content, response.Content.Headers.ContentLength, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

/// <summary>Starts a process without waiting for it; faked in tests.</summary>
public interface IProcessStarter
{
    void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory);
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // No shell: CreateProcess runs the .new/.old side files despite their extension, and the files we
        // wrote carry no Mark of the Web, so there is no SmartScreen prompt on restart.
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"{executablePath} did not start.");
        }

        process.Dispose();
    }
}

/// <summary>Waits between retries; faked in tests so they do not sleep.</summary>
public interface IDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemDelay : IDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
