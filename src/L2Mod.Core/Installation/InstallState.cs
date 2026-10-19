using System.Text.Json;
using System.Text.Json.Serialization;

namespace L2Mod.Core.Installation;

/// <summary>
/// What the launcher did to one client file. <see cref="OriginalSha256"/> names the backup; <see cref="PatchedSha256"/>
/// is the hash of the patch we installed last (null after a restore); <see cref="RecipeKey"/> identifies the
/// operations that patch was built from.
/// </summary>
public sealed record InstalledFile(string OriginalSha256, string? PatchedSha256, string? RecipeKey);

/// <summary>Contents of state.json, keyed by whitelisted file name.</summary>
public sealed record InstallState(Dictionary<string, InstalledFile> Files)
{
    public static InstallState Empty
    {
        get
        {
            return new InstallState(new Dictionary<string, InstalledFile>());
        }
    }

    public InstalledFile? Get(string fileName)
    {
        if (Files.TryGetValue(fileName, out InstalledFile? file))
        {
            return file;
        }

        return null;
    }
}

public sealed class InstallStateStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _path;

    public InstallStateStore(IFileSystem fileSystem, string path)
    {
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>The saved state; empty when there is none. A damaged file is an error, not a reset.</summary>
    public InstallState Load()
    {
        if (!_fileSystem.FileExists(_path))
        {
            return InstallState.Empty;
        }

        try
        {
            InstallState? state = JsonSerializer.Deserialize(_fileSystem.ReadAllBytes(_path), InstallStateJsonContext.Default.InstallState);
            if (state?.Files is null)
            {
                throw new InvalidDataException($"{_path} is empty.");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{_path} is damaged: {exception.Message}", exception);
        }
    }

    public void Save(InstallState state)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, InstallStateJsonContext.Default.InstallState);
        VerifiedWrite.Write(_fileSystem, _path, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallState))]
internal sealed partial class InstallStateJsonContext : JsonSerializerContext
{
}
