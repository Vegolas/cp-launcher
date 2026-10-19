using System.Text;

namespace L2Mod.Core.Tests.Publishing;

/// <summary>A fresh folder under %TEMP%\l2mod-tests, deleted on dispose. Never inside the repo or a client folder.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "l2mod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts)
    {
        return Path.Combine(new[] { Root }.Concat(parts).ToArray());
    }

    public string WriteText(string relativePath, string text)
    {
        return WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(text));
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        string path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // Best effort; a locked file only leaves a stray temp folder.
        }
    }
}
