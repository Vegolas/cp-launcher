using System.Text.Json;
using System.Text.Json.Serialization;

namespace L2Mod.Core.Installation;

/// <summary>
/// What the launcher did to one client file. <see cref="OriginalSha256"/> names the backup; <see cref="PatchedSha256"/>
/// is the hash of the patch we installed last (null after a restore); <see cref="RecipeKey"/> identifies the
/// operations that patch was built from.
/// </summary>
public sealed record InstalledFile(string OriginalSha256, string? PatchedSha256, string? RecipeKey);

/// <summary>The state of one client folder, keyed by whitelisted file name.</summary>
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

/// <summary>
/// state.json, shared by every client folder used with the same data folder: one <see cref="InstallState"/> per
/// client folder (<see cref="InstallerOptions.ClientKey"/>), so switching folders never mixes their states.
/// Backups stay shared, they are named by the hash of the original.
/// <para>
/// A state.json from before the per-client sections has only a top-level <c>files</c>. It belonged to the one
/// client folder the launcher had then, so the first client folder that loads it adopts it, and its next save
/// moves it into its own section.
/// </para>
/// </summary>
public sealed class InstallStateStore
{
    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly string _clientKey;

    public InstallStateStore(IFileSystem fileSystem, string path, string clientDirectory)
    {
        _fileSystem = fileSystem;
        _path = path;
        _clientKey = InstallerOptions.GetClientKey(clientDirectory);
    }

    /// <summary>The saved state of this client folder; empty when there is none. A damaged file is an error,
    /// not a reset.</summary>
    public InstallState Load()
    {
        InstallStateDocument document = ReadDocument();
        InstallState? own = FindClient(document);
        if (own is not null)
        {
            return own;
        }

        if (document.Files is { Count: > 0 } legacy)
        {
            return new InstallState(new Dictionary<string, InstalledFile>(legacy));
        }

        return InstallState.Empty;
    }

    /// <summary>Replaces this client folder's section; the other sections are kept.</summary>
    public void Save(InstallState state)
    {
        InstallStateDocument document = ReadDocument();
        var clients = new Dictionary<string, InstallState>(StringComparer.OrdinalIgnoreCase);
        if (document.Clients is not null)
        {
            foreach (KeyValuePair<string, InstallState> client in document.Clients)
            {
                if (client.Value?.Files is not null && !string.Equals(client.Key, _clientKey, StringComparison.OrdinalIgnoreCase))
                {
                    clients[client.Key] = client.Value;
                }
            }
        }

        clients[_clientKey] = state;
        var updated = new InstallStateDocument { Clients = clients };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(updated, InstallStateJsonContext.Default.InstallStateDocument);
        VerifiedWrite.Write(_fileSystem, _path, json);
    }

    private InstallStateDocument ReadDocument()
    {
        if (!_fileSystem.FileExists(_path))
        {
            return new InstallStateDocument();
        }

        try
        {
            InstallStateDocument? document = JsonSerializer.Deserialize(_fileSystem.ReadAllBytes(_path), InstallStateJsonContext.Default.InstallStateDocument);
            if (document is null || (document.Files is null && document.Clients is null))
            {
                throw new InvalidDataException($"{_path} is empty.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{_path} is damaged: {exception.Message}", exception);
        }
    }

    private InstallState? FindClient(InstallStateDocument document)
    {
        if (document.Clients is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, InstallState> client in document.Clients)
        {
            if (!string.Equals(client.Key, _clientKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (client.Value?.Files is null)
            {
                throw new InvalidDataException($"{_path} is damaged: the section of {client.Key} has no files.");
            }

            return client.Value;
        }

        return null;
    }
}

/// <summary>The contents of state.json.</summary>
internal sealed record InstallStateDocument
{
    /// <summary>The single state of a state.json from before the per-client sections; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, InstalledFile>? Files { get; init; }

    /// <summary>Keyed by <see cref="InstallerOptions.ClientKey"/>.</summary>
    public Dictionary<string, InstallState>? Clients { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallStateDocument))]
internal sealed partial class InstallStateJsonContext : JsonSerializerContext
{
}
