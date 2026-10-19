using System.Text.RegularExpressions;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Installation;

/// <summary>Assets downloaded into a local folder, read by their manifest path ("assets/&lt;sha256&gt;.png" only).</summary>
public sealed partial class DirectoryAssetStore : IAssetStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;

    public DirectoryAssetStore(IFileSystem fileSystem, string root)
    {
        _fileSystem = fileSystem;
        _root = root;
    }

    public byte[] ReadAsset(string path)
    {
        if (!AssetPathPattern().IsMatch(path))
        {
            throw new ArgumentException($"'{path}' is not an asset path.");
        }

        string fullPath = Path.Combine(_root, "assets", Path.GetFileName(path));
        if (!_fileSystem.FileExists(fullPath))
        {
            throw new FileNotFoundException($"Asset {path} has not been downloaded.", fullPath);
        }

        return _fileSystem.ReadAllBytes(fullPath);
    }

    [GeneratedRegex("^assets/[0-9a-f]{64}\\.png\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathPattern();
}
