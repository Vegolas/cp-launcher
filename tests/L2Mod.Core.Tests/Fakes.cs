using L2Mod.Core.Installation;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Tests;

/// <summary>Case-insensitive in-memory file system that records every path written to.</summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    public List<string> WrittenPaths { get; } = new List<string>();

    public bool FileExists(string path)
    {
        return _files.ContainsKey(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        if (!_files.TryGetValue(path, out byte[]? bytes))
        {
            throw new FileNotFoundException($"{path} not found.", path);
        }

        return (byte[])bytes.Clone();
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        _files[path] = (byte[])bytes.Clone();
        WrittenPaths.Add(path);
    }

    public void MoveFile(string source, string destination)
    {
        byte[] bytes = ReadAllBytes(source);
        _files.Remove(source);
        _files[destination] = bytes;
        WrittenPaths.Add(destination);
    }

    public void DeleteFile(string path)
    {
        _files.Remove(path);
    }

    public void CreateDirectory(string path)
    {
    }

    /// <summary>Sets a file without recording it as a write (test setup).</summary>
    public void Seed(string path, byte[] bytes)
    {
        _files[path] = (byte[])bytes.Clone();
    }
}

public sealed class FakeProcessMonitor : IProcessMonitor
{
    public List<string> Running { get; } = new List<string>();

    public IReadOnlyList<string> FindRunning(IReadOnlyCollection<string> processNames)
    {
        var running = new List<string>();
        foreach (string name in processNames)
        {
            if (Running.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                running.Add(name);
            }
        }

        return running;
    }
}

public sealed class InMemoryAssetStore : IAssetStore
{
    private readonly Dictionary<string, byte[]> _assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>Stores the bytes and returns the manifest path ("assets/&lt;sha256&gt;.png").</summary>
    public string Add(byte[] png)
    {
        string path = $"assets/{ContentHash.Sha256Hex(png)}.png";
        _assets[path] = png;
        return path;
    }

    public void Replace(string path, byte[] bytes)
    {
        _assets[path] = bytes;
    }

    public byte[] ReadAsset(string path)
    {
        if (!_assets.TryGetValue(path, out byte[]? bytes))
        {
            throw new FileNotFoundException($"{path} not found.", path);
        }

        return bytes;
    }
}
