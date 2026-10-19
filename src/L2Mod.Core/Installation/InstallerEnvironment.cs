using System.Diagnostics;

namespace L2Mod.Core.Installation;

/// <summary>The file operations the installer needs; faked in tests.</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>Moves a file, replacing an existing destination (atomic on the same volume).</summary>
    void MoveFile(string source, string destination);

    void DeleteFile(string path);

    void CreateDirectory(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        return File.ReadAllBytes(path);
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
    }

    public void MoveFile(string source, string destination)
    {
        File.Move(source, destination, true);
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
    }

    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }
}

/// <summary>Tells whether given processes are running; faked in tests.</summary>
public interface IProcessMonitor
{
    /// <summary>The names from <paramref name="processNames"/> that have a running process (case-insensitive).</summary>
    IReadOnlyList<string> FindRunning(IReadOnlyCollection<string> processNames);
}

public sealed class SystemProcessMonitor : IProcessMonitor
{
    public IReadOnlyList<string> FindRunning(IReadOnlyCollection<string> processNames)
    {
        var running = new List<string>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                foreach (string candidate in processNames)
                {
                    if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) && !running.Contains(candidate))
                    {
                        running.Add(candidate);
                    }
                }
            }
        }

        return running;
    }
}

/// <summary>Processes of the game client; while any runs, nothing is applied or restored.</summary>
public static class GameProcesses
{
    /// <summary>L2.exe shows up as "L2"; the real client binary L2.bin keeps its extension in the process name.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            return new[] { "L2", "L2.bin" };
        }
    }

    /// <summary>A user-facing message when the game runs, otherwise null.</summary>
    public static string? GetBlockingMessage(IProcessMonitor processes)
    {
        IReadOnlyList<string> running = processes.FindRunning(Names);
        if (running.Count == 0)
        {
            return null;
        }

        return $"The game is running ({string.Join(", ", running)}); close it first.";
    }
}

/// <summary>Where the launcher keeps its data and where the client files are.</summary>
public sealed record InstallerOptions(string ClientDirectory, string DataDirectory)
{
    /// <summary>%LocalAppData%\CpLauncher</summary>
    public static string DefaultDataDirectory
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CpLauncher");
        }
    }

    public string StatePath
    {
        get
        {
            return Path.Combine(DataDirectory, "state.json");
        }
    }

    public string BackupDirectory
    {
        get
        {
            return Path.Combine(DataDirectory, "backups");
        }
    }

    public string OptionIniPath
    {
        get
        {
            return Path.Combine(ClientDirectory, ClientFiles.OptionIniPath);
        }
    }

    public string OptionIniBackupPath
    {
        get
        {
            return Path.Combine(BackupDirectory, "Option.ini");
        }
    }

    public string GetClientPath(string fileName)
    {
        return Path.Combine(ClientDirectory, ClientFiles.GetRelativePath(fileName));
    }

    /// <summary>backups\&lt;sha256&gt;\Icon.utx – keeps the file name, which Ver121 decryption depends on.</summary>
    public string GetBackupPath(string fileName, string sha256)
    {
        return Path.Combine(BackupDirectory, sha256.ToLowerInvariant(), fileName);
    }
}

/// <summary>Writes through a temporary file next to the target, then reads back and checks the hash.</summary>
public static class VerifiedWrite
{
    public const string TemporarySuffix = ".cplauncher-tmp";

    public static void Write(IFileSystem fileSystem, string path, byte[] bytes)
    {
        string expected = ContentHash.Sha256Hex(bytes);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }

        string temporary = path + TemporarySuffix;
        if (fileSystem.FileExists(temporary))
        {
            fileSystem.DeleteFile(temporary);
        }

        fileSystem.WriteAllBytes(temporary, bytes);
        fileSystem.MoveFile(temporary, path);
        if (!ContentHash.Matches(expected, fileSystem.ReadAllBytes(path)))
        {
            throw new IOException($"{path} does not read back as written.");
        }
    }
}
