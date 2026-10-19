using System.Net;
using L2Mod.Core.Manifests;
using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests.Updates;

/// <summary>
/// Case-insensitive in-memory file system with Windows-like failures: a locked file cannot be deleted, renamed
/// or created (<see cref="Lock"/>), single renames can be made to fail (<see cref="FailMove"/>). Records every
/// change in <see cref="Operations"/>.
/// </summary>
public sealed class InMemoryUpdateFileSystem : IUpdateFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LockInfo> _locks = new Dictionary<string, LockInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Source, string Destination), int> _failingMoves = new Dictionary<(string Source, string Destination), int>();
    private readonly Dictionary<string, int> _inUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public List<string> Operations { get; } = new List<string>();

    public bool FileExists(string path)
    {
        return _files.ContainsKey(path);
    }

    public Stream CreateNew(string path)
    {
        if (_files.ContainsKey(path))
        {
            throw new IOException($"{path} already exists.");
        }

        ThrowIfLocked(path);
        _files[path] = Array.Empty<byte>();
        Operations.Add($"create {path}");
        return new CommittingStream(this, path);
    }

    public Stream OpenRead(string path)
    {
        return new MemoryStream(Read(path), false);
    }

    public void Move(string source, string destination)
    {
        if (!_files.TryGetValue(source, out byte[]? bytes))
        {
            throw new FileNotFoundException($"{source} not found.", source);
        }

        if (_files.ContainsKey(destination))
        {
            throw new IOException($"{destination} already exists.");
        }

        ThrowIfLocked(source);
        if (_failingMoves.TryGetValue((source, destination), out int failures) && failures > 0)
        {
            _failingMoves[(source, destination)] = failures == int.MaxValue ? failures : failures - 1;
            throw new IOException($"Simulated failure moving {source} to {destination}.");
        }

        _files.Remove(source);
        _files[destination] = bytes;
        Operations.Add($"move {source} -> {destination}");
    }

    public void Delete(string path)
    {
        if (!_files.ContainsKey(path))
        {
            return;
        }

        ThrowIfLocked(path);
        _files.Remove(path);
        Operations.Add($"delete {path}");
    }

    public bool IsInUse(string path)
    {
        if (!_inUse.TryGetValue(path, out int remaining) || remaining <= 0)
        {
            return false;
        }

        if (remaining != int.MaxValue)
        {
            _inUse[path] = remaining - 1;
        }

        return true;
    }

    /// <summary>The next <paramref name="checks"/> calls of <see cref="IsInUse"/> say the file runs.</summary>
    public void MarkInUse(string path, int checks = int.MaxValue)
    {
        _inUse[path] = checks;
    }

    /// <summary>Sets a file without recording it (test setup).</summary>
    public void Seed(string path, byte[] bytes)
    {
        _files[path] = (byte[])bytes.Clone();
    }

    public byte[] Read(string path)
    {
        if (!_files.TryGetValue(path, out byte[]? bytes))
        {
            throw new FileNotFoundException($"{path} not found.", path);
        }

        return (byte[])bytes.Clone();
    }

    /// <summary>The next <paramref name="failures"/> deletes/renames fail; a running exe fails with access denied.</summary>
    public void Lock(string path, int failures = int.MaxValue, bool runningExecutable = false)
    {
        _locks[path] = new LockInfo(failures, runningExecutable);
    }

    public void Unlock(string path)
    {
        _locks.Remove(path);
    }

    public void FailMove(string source, string destination, int failures = int.MaxValue)
    {
        _failingMoves[(source, destination)] = failures;
    }

    private void ThrowIfLocked(string path)
    {
        if (!_locks.TryGetValue(path, out LockInfo? info) || info.Remaining <= 0)
        {
            return;
        }

        if (info.Remaining != int.MaxValue)
        {
            info.Remaining--;
        }

        if (info.RunningExecutable)
        {
            throw new UnauthorizedAccessException($"Access to {path} is denied.");
        }

        throw new IOException($"{path} is being used by another process.");
    }

    private sealed class LockInfo
    {
        public LockInfo(int remaining, bool runningExecutable)
        {
            Remaining = remaining;
            RunningExecutable = runningExecutable;
        }

        public int Remaining { get; set; }

        public bool RunningExecutable { get; }
    }

    /// <summary>Stores its content in the file system when disposed, like closing a file.</summary>
    private sealed class CommittingStream : MemoryStream
    {
        private readonly InMemoryUpdateFileSystem _owner;
        private readonly string _path;

        public CommittingStream(InMemoryUpdateFileSystem owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _owner._files.ContainsKey(_path))
            {
                _owner._files[_path] = ToArray();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Serves bytes or streams by URI; anything else is a 404.</summary>
public sealed class FakeDownloadClient : IDownloadClient
{
    private readonly Dictionary<Uri, Func<DownloadResponse>> _responses = new Dictionary<Uri, Func<DownloadResponse>>();

    public List<Uri> Requests { get; } = new List<Uri>();

    public void Serve(Uri uri, byte[] body, bool announceLength = true)
    {
        long? length = announceLength ? body.Length : null;
        _responses[uri] = () => new DownloadResponse(new MemoryStream(body, false), length);
    }

    public void ServeStream(Uri uri, Func<Stream> body, long? announcedLength)
    {
        _responses[uri] = () => new DownloadResponse(body(), announcedLength);
    }

    public Task<DownloadResponse> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(uri);
        if (!_responses.TryGetValue(uri, out Func<DownloadResponse>? response))
        {
            throw new HttpRequestException($"{uri} returned 404 Not Found.", null, HttpStatusCode.NotFound);
        }

        return Task.FromResult(response());
    }
}

/// <summary>Returns the data in small reads, then fails or stalls instead of ending.</summary>
public sealed class ScriptedStream : Stream
{
    private readonly byte[] _data;
    private readonly int _failAfter;
    private readonly bool _stall;
    private int _position;

    private ScriptedStream(byte[] data, int failAfter, bool stall)
    {
        _data = data;
        _failAfter = failAfter;
        _stall = stall;
    }

    public override bool CanRead
    {
        get
        {
            return true;
        }
    }

    public override bool CanSeek
    {
        get
        {
            return false;
        }
    }

    public override bool CanWrite
    {
        get
        {
            return false;
        }
    }

    public override long Length
    {
        get
        {
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get
        {
            throw new NotSupportedException();
        }

        set
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Delivers <paramref name="count"/> bytes of <paramref name="data"/>, then the connection drops.</summary>
    public static ScriptedStream FailingAfter(byte[] data, int count)
    {
        return new ScriptedStream(data, count, false);
    }

    /// <summary>Delivers <paramref name="count"/> bytes of <paramref name="data"/>, then waits forever.</summary>
    public static ScriptedStream StallingAfter(byte[] data, int count)
    {
        return new ScriptedStream(data, count, true);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _failAfter)
        {
            if (_stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            throw new IOException("The connection was reset.");
        }

        int count = Math.Min(Math.Min(buffer.Length, 4096), _failAfter - _position);
        _data.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

public sealed record StartedProcess(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);

public sealed class RecordingProcessStarter : IProcessStarter
{
    public List<StartedProcess> Started { get; } = new List<StartedProcess>();

    /// <summary>Thrown instead of starting (antivirus or policy blocks the new exe).</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many starts fail with <see cref="Failure"/>; the rest succeed.</summary>
    public int FailuresLeft { get; set; } = int.MaxValue;

    /// <summary>Runs before the start is recorded, to look at the files at that moment.</summary>
    public Action<string>? OnStart { get; set; }

    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        OnStart?.Invoke(executablePath);
        if (Failure is not null && FailuresLeft > 0)
        {
            FailuresLeft--;
            throw Failure;
        }

        Started.Add(new StartedProcess(executablePath, arguments.ToArray(), workingDirectory));
    }
}

/// <summary>Records waits instead of sleeping; <see cref="OnWait"/> gets the 1-based wait number.</summary>
public sealed class RecordingDelay : IDelay
{
    public List<TimeSpan> Waits { get; } = new List<TimeSpan>();

    public Action<int>? OnWait { get; set; }

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waits.Add(delay);
        OnWait?.Invoke(Waits.Count);
        return Task.CompletedTask;
    }
}

public sealed class FakeInstanceLock : IInstanceLock
{
    public bool IsHeld { get; private set; } = true;

    public void Dispose()
    {
        IsHeld = false;
    }
}

public sealed class CollectingProgress : IProgress<DownloadProgress>
{
    public List<DownloadProgress> Reports { get; } = new List<DownloadProgress>();

    public void Report(DownloadProgress value)
    {
        Reports.Add(value);
    }
}

public static class UpdateTestData
{
    public static readonly Uri BaseUri = new Uri("https://cdn.example.invalid/cp/");
    public static readonly LauncherVersion V100 = LauncherVersion.Parse("1.0.0");
    public static readonly LauncherVersion V101 = LauncherVersion.Parse("1.0.1");

    public static UpdateStartupInfo InstallUpdateOver100
    {
        get
        {
            return UpdateStartupInfo.Parse(new[] { "--install-update", "1.0.0" });
        }
    }

    public static UpdateStartupInfo InstallRollbackOver101
    {
        get
        {
            return UpdateStartupInfo.Parse(new[] { "--install-rollback", "1.0.1" });
        }
    }

    public const string ReleaseUrl = "launcher/CpLauncher-1.0.1.exe";

    public static Uri ReleaseUri
    {
        get
        {
            return new Uri(BaseUri, ReleaseUrl);
        }
    }

    /// <summary>A dummy exe: "MZ" plus deterministic filler, several download buffers long.</summary>
    public static byte[] Executable(int seed, int length = 300_000)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    public static LauncherRelease Release(byte[] body, string version = "1.0.1", string url = ReleaseUrl)
    {
        return new LauncherRelease(version, url, ContentHash.Sha256Hex(body));
    }

    public static Manifest Manifest(LauncherRelease? release, bool enabled = true)
    {
        return new Manifest(
            1,
            "2026.10.20.1",
            enabled,
            release,
            new Dictionary<string, IReadOnlyList<string>>(),
            Array.Empty<Announcement>(),
            Array.Empty<Pack>(),
            Array.Empty<OptionPreset>());
    }

    /// <summary>Default options (retries go through the test's <see cref="RecordingDelay"/>).</summary>
    public static UpdateOptions Options(string executablePath, LauncherVersion current)
    {
        return new UpdateOptions(executablePath, current, BaseUri);
    }
}
