using System.Text;
using L2Mod.Core.Manifests;
using L2Mod.Core.Operations;
using L2Mod.Core.Packages;

namespace L2Mod.Core.Installation;

/// <summary>What the client file currently is, judged by its SHA-256.</summary>
public enum PatchFileState
{
    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The hash is in the manifest's supportedOriginals.</summary>
    Original,

    /// <summary>The hash is the patch we installed last.</summary>
    OurPatch,

    /// <summary>Anything else: Dex updated the file, another mod, or an old manual install.</summary>
    Unknown,
}

public enum PendingAction
{
    None,

    /// <summary>Back up the original, build the patch from it, install.</summary>
    Install,

    /// <summary>Recipes or selected packs changed: rebuild from the backup of the original.</summary>
    Rebuild,

    /// <summary>No operations apply any more (kill switch or no packs selected): put the original back.</summary>
    Restore,
}

public sealed record PatchFileStatus(
    string FileName,
    PatchFileState State,
    string? CurrentSha256,
    bool HasBackup,
    PendingAction Pending,
    string Message);

public enum PatchOutcome
{
    Unchanged,
    Installed,
    Rebuilt,
    Restored,

    /// <summary>Nothing done on purpose (unknown version, missing file, no backup).</summary>
    Skipped,

    /// <summary>The game is running.</summary>
    Blocked,
    Failed,
}

public sealed record PatchFileResult(string FileName, PatchOutcome Outcome, string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// Installs patches built from the manifest onto the whitelisted client files, one state machine per file:
/// <list type="bullet">
/// <item>supported original: back it up (once per hash), build the patch from it, install, verify the hash;</item>
/// <item>our last patch: rebuild from the backup when the recipe key changed, restore it when no operations
/// apply any more (kill switch <c>enabled: false</c> or no packs selected), otherwise leave it;</item>
/// <item>unknown version: leave it alone; <see cref="Restore"/> still works when a backup exists.</item>
/// </list>
/// Nothing is applied or restored while the game runs.
/// </summary>
public sealed class PatchInstaller
{
    /// <summary>Part of the recipe key; bump it when encoding changes, so installed patches get rebuilt.</summary>
    public const int EngineVersion = 1;

    private readonly InstallerOptions _options;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessMonitor _processes;
    private readonly InstallStateStore _store;

    public PatchInstaller(InstallerOptions options, IFileSystem fileSystem, IProcessMonitor processes)
    {
        _options = options;
        _fileSystem = fileSystem;
        _processes = processes;
        _store = new InstallStateStore(fileSystem, options.StatePath);
    }

    /// <summary>Identifies a set of operations (order matters) together with the engine version.</summary>
    public static string ComputeRecipeKey(IReadOnlyList<IIconOperation> operations)
    {
        var text = new StringBuilder();
        text.Append("engine ").Append(EngineVersion).Append('\n');
        foreach (IIconOperation operation in operations)
        {
            text.Append(operation.Describe()).Append('\n');
        }

        return ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public IReadOnlyList<PatchFileStatus> GetStatus(Manifest manifest, IReadOnlyCollection<string> selectedPackIds)
    {
        InstallState state = _store.Load();
        var statuses = new List<PatchFileStatus>();
        foreach (string fileName in ClientFiles.Patchable)
        {
            statuses.Add(Inspect(fileName, manifest, selectedPackIds, state).Status);
        }

        return statuses;
    }

    public IReadOnlyList<PatchFileResult> Apply(Manifest manifest, IReadOnlyCollection<string> selectedPackIds, IAssetStore assets)
    {
        string? blocked = GameProcesses.GetBlockingMessage(_processes);
        if (blocked is not null)
        {
            return BlockAll(blocked);
        }

        var results = new List<PatchFileResult>();
        foreach (string fileName in ClientFiles.Patchable)
        {
            try
            {
                InstallState state = _store.Load();
                Inspection inspection = Inspect(fileName, manifest, selectedPackIds, state);
                results.Add(Execute(inspection, state, assets));
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                results.Add(new PatchFileResult(fileName, PatchOutcome.Failed, exception.Message, Array.Empty<string>()));
            }
        }

        return results;
    }

    /// <summary>"Restore original": puts the backed-up original back wherever the file differs from it.</summary>
    public IReadOnlyList<PatchFileResult> Restore()
    {
        string? blocked = GameProcesses.GetBlockingMessage(_processes);
        if (blocked is not null)
        {
            return BlockAll(blocked);
        }

        var results = new List<PatchFileResult>();
        foreach (string fileName in ClientFiles.Patchable)
        {
            try
            {
                results.Add(RestoreFile(fileName));
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                results.Add(new PatchFileResult(fileName, PatchOutcome.Failed, exception.Message, Array.Empty<string>()));
            }
        }

        return results;
    }

    private Inspection Inspect(string fileName, Manifest manifest, IReadOnlyCollection<string> selectedPackIds, InstallState state)
    {
        InstalledFile? installed = state.Get(fileName);
        bool hasBackup = installed is not null && _fileSystem.FileExists(_options.GetBackupPath(fileName, installed.OriginalSha256));
        string path = _options.GetClientPath(fileName);
        if (!_fileSystem.FileExists(path))
        {
            var missing = new PatchFileStatus(fileName, PatchFileState.Missing, null, hasBackup, PendingAction.None, $"{ClientFiles.GetRelativePath(fileName)} was not found in the client folder.");
            return new Inspection(missing, null, Array.Empty<IIconOperation>(), string.Empty, installed);
        }

        byte[] current = _fileSystem.ReadAllBytes(path);
        string hash = ContentHash.Sha256Hex(current);
        bool readable = manifest.Schema == ManifestParser.SupportedSchema;
        IReadOnlyList<IIconOperation> operations = Array.Empty<IIconOperation>();
        if (manifest.Enabled && readable)
        {
            operations = manifest.GetOperations(fileName, selectedPackIds);
        }

        string recipeKey = ComputeRecipeKey(operations);
        PatchFileState fileState;
        PendingAction pending;
        string message;
        if (manifest.IsSupportedOriginal(fileName, hash))
        {
            fileState = PatchFileState.Original;
            pending = PendingAction.None;
            message = $"{fileName} is the original; nothing to install.";
            if (!manifest.Enabled)
            {
                message = $"{fileName} is the original; patches are switched off in the manifest.";
            }
            else if (operations.Count > 0)
            {
                pending = PendingAction.Install;
                message = $"{fileName} is the original; the selected packs are not installed yet.";
            }
        }
        else if (installed?.PatchedSha256 is not null && string.Equals(installed.PatchedSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            fileState = PatchFileState.OurPatch;
            (pending, message) = PlanForOurPatch(fileName, manifest, readable, operations, recipeKey, installed);
        }
        else
        {
            fileState = PatchFileState.Unknown;
            pending = PendingAction.None;
            message = $"Unknown version of {fileName}; waiting for a recipe update.";
            if (hasBackup)
            {
                message += " The backed-up original can be restored.";
            }
        }

        var status = new PatchFileStatus(fileName, fileState, hash, hasBackup, pending, message);
        return new Inspection(status, current, operations, recipeKey, installed);
    }

    private (PendingAction Pending, string Message) PlanForOurPatch(
        string fileName,
        Manifest manifest,
        bool readable,
        IReadOnlyList<IIconOperation> operations,
        string recipeKey,
        InstalledFile installed)
    {
        if (!manifest.Enabled)
        {
            return (PendingAction.Restore, $"Patches are switched off in the manifest; the original {fileName} will be restored.");
        }

        if (!readable)
        {
            return (PendingAction.None, $"Our patch of {fileName} stays as it is until the launcher is updated.");
        }

        if (operations.Count == 0)
        {
            return (PendingAction.Restore, $"No selected pack changes {fileName}; the original will be restored.");
        }

        if (string.Equals(installed.RecipeKey, recipeKey, StringComparison.Ordinal))
        {
            return (PendingAction.None, $"Our patch of {fileName} is up to date.");
        }

        if (!manifest.IsSupportedOriginal(fileName, installed.OriginalSha256))
        {
            return (PendingAction.None, $"Our patch of {fileName} was built from an original this manifest no longer supports; restore it or wait for a recipe update.");
        }

        return (PendingAction.Rebuild, $"Recipes or selected packs changed; {fileName} will be rebuilt from the original.");
    }

    private PatchFileResult Execute(Inspection inspection, InstallState state, IAssetStore assets)
    {
        PatchFileStatus status = inspection.Status;
        string fileName = status.FileName;
        switch (status.Pending)
        {
            case PendingAction.Install:
            {
                byte[] original = inspection.Current!;
                string originalHash = status.CurrentSha256!;
                EnsureBackup(fileName, original, originalHash);
                (byte[] patched, IReadOnlyList<string> warnings) = BuildPatch(fileName, original, inspection.Operations, assets);
                Install(fileName, patched, originalHash, inspection.RecipeKey, state);
                return new PatchFileResult(fileName, PatchOutcome.Installed, $"Patch of {fileName} installed ({inspection.Operations.Count} operations).", warnings);
            }

            case PendingAction.Rebuild:
            {
                string originalHash = inspection.Installed!.OriginalSha256;
                byte[] original = ReadBackup(fileName, originalHash);
                (byte[] patched, IReadOnlyList<string> warnings) = BuildPatch(fileName, original, inspection.Operations, assets);
                if (ContentHash.Matches(status.CurrentSha256!, patched))
                {
                    state.Files[fileName] = inspection.Installed with { RecipeKey = inspection.RecipeKey };
                    _store.Save(state);
                    return new PatchFileResult(fileName, PatchOutcome.Unchanged, $"Our patch of {fileName} already matches the recipes.", warnings);
                }

                Install(fileName, patched, originalHash, inspection.RecipeKey, state);
                return new PatchFileResult(fileName, PatchOutcome.Rebuilt, $"Patch of {fileName} rebuilt ({inspection.Operations.Count} operations).", warnings);
            }

            case PendingAction.Restore:
            {
                RestoreBackup(fileName, inspection.Installed!, state);
                return new PatchFileResult(fileName, PatchOutcome.Restored, status.Message, Array.Empty<string>());
            }

            default:
            {
                PatchOutcome outcome = PatchOutcome.Unchanged;
                if (status.State == PatchFileState.Unknown || status.State == PatchFileState.Missing)
                {
                    outcome = PatchOutcome.Skipped;
                }

                return new PatchFileResult(fileName, outcome, status.Message, Array.Empty<string>());
            }
        }
    }

    private PatchFileResult RestoreFile(string fileName)
    {
        InstallState state = _store.Load();
        InstalledFile? installed = state.Get(fileName);
        if (installed is null || !_fileSystem.FileExists(_options.GetBackupPath(fileName, installed.OriginalSha256)))
        {
            return new PatchFileResult(fileName, PatchOutcome.Skipped, $"There is no backup of the original {fileName}.", Array.Empty<string>());
        }

        string path = _options.GetClientPath(fileName);
        if (_fileSystem.FileExists(path) && ContentHash.Matches(installed.OriginalSha256, _fileSystem.ReadAllBytes(path)))
        {
            return new PatchFileResult(fileName, PatchOutcome.Unchanged, $"{fileName} already is the original.", Array.Empty<string>());
        }

        RestoreBackup(fileName, installed, state);
        return new PatchFileResult(fileName, PatchOutcome.Restored, $"Original {fileName} restored.", Array.Empty<string>());
    }

    private (byte[] Bytes, IReadOnlyList<string> Warnings) BuildPatch(string fileName, byte[] original, IReadOnlyList<IIconOperation> operations, IAssetStore assets)
    {
        var package = new IconPackage(Path.GetFileNameWithoutExtension(fileName), L2File.FromBytes(original));
        OperationBuildResult build = OperationComposer.Build(package, operations, assets);
        return (build.Replacement.FileBytes, build.Warnings);
    }

    private void Install(string fileName, byte[] patched, string originalHash, string recipeKey, InstallState state)
    {
        VerifiedWrite.Write(_fileSystem, _options.GetClientPath(fileName), patched);
        state.Files[fileName] = new InstalledFile(originalHash, ContentHash.Sha256Hex(patched), recipeKey);
        _store.Save(state);
    }

    private void RestoreBackup(string fileName, InstalledFile installed, InstallState state)
    {
        byte[] original = ReadBackup(fileName, installed.OriginalSha256);
        VerifiedWrite.Write(_fileSystem, _options.GetClientPath(fileName), original);
        state.Files[fileName] = installed with { PatchedSha256 = null, RecipeKey = null };
        _store.Save(state);
    }

    private void EnsureBackup(string fileName, byte[] original, string originalHash)
    {
        string backupPath = _options.GetBackupPath(fileName, originalHash);
        if (_fileSystem.FileExists(backupPath) && ContentHash.Matches(originalHash, _fileSystem.ReadAllBytes(backupPath)))
        {
            return;
        }

        VerifiedWrite.Write(_fileSystem, backupPath, original);
    }

    private byte[] ReadBackup(string fileName, string originalHash)
    {
        string backupPath = _options.GetBackupPath(fileName, originalHash);
        if (!_fileSystem.FileExists(backupPath))
        {
            throw new InvalidDataException($"The backup of the original {fileName} is missing ({backupPath}).");
        }

        byte[] original = _fileSystem.ReadAllBytes(backupPath);
        if (!ContentHash.Matches(originalHash, original))
        {
            throw new InvalidDataException($"The backup of the original {fileName} is damaged ({backupPath}).");
        }

        return original;
    }

    private static IReadOnlyList<PatchFileResult> BlockAll(string message)
    {
        var results = new List<PatchFileResult>();
        foreach (string fileName in ClientFiles.Patchable)
        {
            results.Add(new PatchFileResult(fileName, PatchOutcome.Blocked, message, Array.Empty<string>()));
        }

        return results;
    }

    private static bool IsExpectedFailure(Exception exception)
    {
        return exception is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException or FormatException;
    }

    private sealed record Inspection(
        PatchFileStatus Status,
        byte[]? Current,
        IReadOnlyList<IIconOperation> Operations,
        string RecipeKey,
        InstalledFile? Installed);
}
