using L2Mod.Core;
using L2Mod.Core.Installation;

namespace CpLauncher.Services;

public sealed record ClientFolderCheck(string? Directory, IReadOnlyList<string> Errors)
{
    public bool IsValid
    {
        get
        {
            return Errors.Count == 0;
        }
    }
}

/// <summary>A folder is an L2Dex client when it has SysTextures\Icon.utx and system_season\L2.exe.</summary>
public static class ClientFolder
{
    public static string IconRelativePath
    {
        get
        {
            return ClientFiles.GetRelativePath(ClientFiles.IconUtx);
        }
    }

    public static string GameBinaryRelativePath
    {
        get
        {
            return Path.Combine("system_season", "L2.exe");
        }
    }

    public static ClientFolderCheck Validate(IFileSystem fileSystem, string? directory)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            errors.Add("Wybierz folder klienta L2Dex.");
            return new ClientFolderCheck(null, errors);
        }

        string trimmed = directory.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(trimmed))
        {
            errors.Add("Podaj pełną ścieżkę folderu, np. C:\\Lineage2Dex.");
            return new ClientFolderCheck(trimmed, errors);
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (!fileSystem.FileExists(Path.Combine(full, IconRelativePath)))
        {
            errors.Add($"Brak pliku {IconRelativePath} – to nie jest folder klienta L2Dex.");
        }

        if (!fileSystem.FileExists(Path.Combine(full, GameBinaryRelativePath)))
        {
            errors.Add($"Brak pliku {GameBinaryRelativePath} – to nie jest folder klienta L2Dex.");
        }

        return new ClientFolderCheck(full, errors);
    }
}
