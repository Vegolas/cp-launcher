using System.Text;
using L2Mod.Core.Effects;
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

/// <summary>
/// Why a file has its <see cref="PatchFileState"/> and <see cref="PendingAction"/>, or why an apply or restore
/// ended with its <see cref="PatchOutcome"/>, so a UI can describe it without deriving it again.
/// </summary>
public enum PatchReason
{
    /// <summary>Not set: the record was not made by <see cref="PatchInstaller"/>.</summary>
    Unspecified,

    /// <summary>The file does not exist in the client folder.</summary>
    FileMissing,

    /// <summary>Neither a supported original nor our last patch: left alone.</summary>
    UnknownVersion,

    /// <summary>The manifest's kill switch is off: nothing is installed, our patch is restored.</summary>
    SwitchedOff,

    /// <summary>The manifest has a newer schema: nothing is installed or rebuilt until the launcher is updated.</summary>
    NeedsNewerLauncher,

    /// <summary>No selected pack changes the file: nothing is installed, our patch is restored.</summary>
    NothingSelected,

    /// <summary>A supported original; the selected packs are not installed yet.</summary>
    NotInstalled,

    /// <summary>Our patch matches the selected packs and the recipes.</summary>
    UpToDate,

    /// <summary>Our patch, but the recipes or the selected packs changed: rebuilt from the backup.</summary>
    Outdated,

    /// <summary>Our patch was built from an original the manifest no longer supports: left as it is.</summary>
    UnsupportedOriginal,

    /// <summary>Restore: there is no backup of the original.</summary>
    NoBackup,

    /// <summary>Restore: the file already is the original.</summary>
    AlreadyOriginal,

    /// <summary>Restore: the original was put back on request.</summary>
    RestoreRequested,

    /// <summary>The game is running; nothing was changed.</summary>
    GameRunning,

    /// <summary>A file or data error; see the message.</summary>
    Error,
}

public sealed record PatchFileStatus(
    string FileName,
    PatchFileState State,
    string? CurrentSha256,
    bool HasBackup,
    PendingAction Pending,
    string Message)
{
    public PatchReason Reason { get; init; }

    /// <summary>How many operations of the selection change this file (0 when switched off or unreadable).</summary>
    public int OperationCount { get; init; }
}

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

public sealed record PatchFileResult(string FileName, PatchOutcome Outcome, string Message, IReadOnlyList<string> Warnings)
{
    public PatchReason Reason { get; init; }
}

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
    /// <summary>Part of Icon.utx's recipe key; bump it when encoding changes, so installed patches get rebuilt.</summary>
    public const int EngineVersion = 1;

    /// <summary>Part of the recipe key of the effect packages (system_season); bump it when the effect engine changes
    /// its output, so installed patches get rebuilt.</summary>
    public const int EffectEngineVersion = 1;

    private readonly InstallerOptions _options;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessMonitor _processes;
    private readonly InstallStateStore _store;

    public PatchInstaller(InstallerOptions options, IFileSystem fileSystem, IProcessMonitor processes)
    {
        _options = options;
        _fileSystem = fileSystem;
        _processes = processes;
        _store = new InstallStateStore(fileSystem, options.StatePath, options.ClientDirectory);
    }

    /// <summary>
    /// Identifies the operations of one file (order matters) together with the version of the engine that builds
    /// it. Icon.utx keeps the key of launchers before the effect packages, so its installed patches stay up to date.
    /// </summary>
    public static string ComputeRecipeKey(string fileName, IReadOnlyList<IPatchOperation> operations)
    {
        var text = new StringBuilder();
        if (ClientFiles.IsIconPackage(fileName))
        {
            text.Append("engine ").Append(EngineVersion).Append('\n');
        }
        else
        {
            text.Append("effect engine ").Append(EffectEngineVersion).Append(' ').Append(fileName).Append('\n');
        }

        foreach (IPatchOperation operation in operations)
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
                results.Add(Execute(inspection, state, assets, manifest));
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                results.Add(Failed(fileName, exception));
            }
        }

        return results;
    }

    /// <summary>
    /// The original of a client file, to draw previews on: the client file when the manifest supports it as an
    /// original, otherwise the verified backup of the original our patch was built from. Null when neither is
    /// there: drawing on our patch or on an unknown version would show wrong (stacked) icons.
    /// Throws <see cref="InvalidDataException"/> when state.json is damaged.
    /// </summary>
    public byte[]? ReadOriginal(Manifest manifest, string fileName)
    {
        string path = _options.GetClientPath(fileName);
        if (_fileSystem.FileExists(path))
        {
            byte[] current = _fileSystem.ReadAllBytes(path);
            if (manifest.IsSupportedOriginal(fileName, ContentHash.Sha256Hex(current)))
            {
                return current;
            }
        }

        InstalledFile? installed = _store.Load().Get(fileName);
        if (installed is null)
        {
            return null;
        }

        string backupPath = _options.GetBackupPath(fileName, installed.OriginalSha256);
        if (!_fileSystem.FileExists(backupPath))
        {
            return null;
        }

        byte[] backup = _fileSystem.ReadAllBytes(backupPath);
        if (!ContentHash.Matches(installed.OriginalSha256, backup))
        {
            return null;
        }

        return backup;
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
                results.Add(Failed(fileName, exception));
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
            var missing = new PatchFileStatus(fileName, PatchFileState.Missing, null, hasBackup, PendingAction.None, $"{ClientFiles.GetRelativePath(fileName)} was not found in the client folder.")
            {
                Reason = PatchReason.FileMissing,
                OperationCount = CountOperations(fileName, manifest, selectedPackIds),
            };
            return new Inspection(missing, null, Array.Empty<IPatchOperation>(), string.Empty, installed);
        }

        byte[] current = _fileSystem.ReadAllBytes(path);
        string hash = ContentHash.Sha256Hex(current);
        bool readable = manifest.Schema == ManifestParser.SupportedSchema;
        IReadOnlyList<IPatchOperation> operations = Array.Empty<IPatchOperation>();
        if (manifest.Enabled && readable)
        {
            operations = manifest.GetOperations(fileName, selectedPackIds);
        }

        string recipeKey = ComputeRecipeKey(fileName, operations);
        PatchFileState fileState;
        Plan plan;
        if (manifest.IsSupportedOriginal(fileName, hash))
        {
            fileState = PatchFileState.Original;
            plan = new Plan(PendingAction.None, PatchReason.NothingSelected, $"{fileName} is the original; nothing to install.");
            if (!manifest.Enabled)
            {
                plan = new Plan(PendingAction.None, PatchReason.SwitchedOff, $"{fileName} is the original; patches are switched off in the manifest.");
            }
            else if (!readable)
            {
                plan = new Plan(PendingAction.None, PatchReason.NeedsNewerLauncher, $"{fileName} is the original; nothing to install.");
            }
            else if (operations.Count > 0)
            {
                plan = new Plan(PendingAction.Install, PatchReason.NotInstalled, $"{fileName} is the original; the selected packs are not installed yet.");
            }
        }
        else if (installed?.PatchedSha256 is not null && string.Equals(installed.PatchedSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            fileState = PatchFileState.OurPatch;
            plan = PlanForOurPatch(fileName, manifest, readable, operations, recipeKey, installed);
        }
        else
        {
            fileState = PatchFileState.Unknown;
            string message = $"Unknown version of {fileName}; waiting for a recipe update.";
            if (hasBackup)
            {
                message += " The backed-up original can be restored.";
            }

            plan = new Plan(PendingAction.None, PatchReason.UnknownVersion, message);
        }

        var status = new PatchFileStatus(fileName, fileState, hash, hasBackup, plan.Pending, plan.Message)
        {
            Reason = plan.Reason,
            OperationCount = operations.Count,
        };
        return new Inspection(status, current, operations, recipeKey, installed);
    }

    private static int CountOperations(string fileName, Manifest manifest, IReadOnlyCollection<string> selectedPackIds)
    {
        if (!manifest.Enabled || manifest.Schema != ManifestParser.SupportedSchema)
        {
            return 0;
        }

        return manifest.GetOperations(fileName, selectedPackIds).Count;
    }

    private static Plan PlanForOurPatch(
        string fileName,
        Manifest manifest,
        bool readable,
        IReadOnlyList<IPatchOperation> operations,
        string recipeKey,
        InstalledFile installed)
    {
        if (!manifest.Enabled)
        {
            return new Plan(PendingAction.Restore, PatchReason.SwitchedOff, $"Patches are switched off in the manifest; the original {fileName} will be restored.");
        }

        if (!readable)
        {
            return new Plan(PendingAction.None, PatchReason.NeedsNewerLauncher, $"Our patch of {fileName} stays as it is until the launcher is updated.");
        }

        if (operations.Count == 0)
        {
            return new Plan(PendingAction.Restore, PatchReason.NothingSelected, $"No selected pack changes {fileName}; the original will be restored.");
        }

        if (string.Equals(installed.RecipeKey, recipeKey, StringComparison.Ordinal))
        {
            return new Plan(PendingAction.None, PatchReason.UpToDate, $"Our patch of {fileName} is up to date.");
        }

        if (!manifest.IsSupportedOriginal(fileName, installed.OriginalSha256))
        {
            return new Plan(PendingAction.None, PatchReason.UnsupportedOriginal, $"Our patch of {fileName} was built from an original this manifest no longer supports; restore it or wait for a recipe update.");
        }

        return new Plan(PendingAction.Rebuild, PatchReason.Outdated, $"Recipes or selected packs changed; {fileName} will be rebuilt from the original.");
    }

    private PatchFileResult Execute(Inspection inspection, InstallState state, IAssetStore assets, Manifest manifest)
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
                (byte[] patched, IReadOnlyList<string> warnings) = BuildPatch(fileName, original, inspection.Operations, assets, manifest);
                Install(fileName, patched, originalHash, inspection.RecipeKey, state);
                return Result(fileName, PatchOutcome.Installed, status.Reason, $"Patch of {fileName} installed ({inspection.Operations.Count} operations).", warnings);
            }

            case PendingAction.Rebuild:
            {
                string originalHash = inspection.Installed!.OriginalSha256;
                byte[] original = ReadBackup(fileName, originalHash);
                (byte[] patched, IReadOnlyList<string> warnings) = BuildPatch(fileName, original, inspection.Operations, assets, manifest);
                if (ContentHash.Matches(status.CurrentSha256!, patched))
                {
                    state.Files[fileName] = inspection.Installed with { RecipeKey = inspection.RecipeKey };
                    _store.Save(state);
                    return Result(fileName, PatchOutcome.Unchanged, PatchReason.UpToDate, $"Our patch of {fileName} already matches the recipes.", warnings);
                }

                Install(fileName, patched, originalHash, inspection.RecipeKey, state);
                return Result(fileName, PatchOutcome.Rebuilt, status.Reason, $"Patch of {fileName} rebuilt ({inspection.Operations.Count} operations).", warnings);
            }

            case PendingAction.Restore:
            {
                RestoreBackup(fileName, inspection.Installed!, state);
                return Result(fileName, PatchOutcome.Restored, status.Reason, status.Message, Array.Empty<string>());
            }

            default:
            {
                PatchOutcome outcome = PatchOutcome.Unchanged;
                if (status.State == PatchFileState.Unknown || status.State == PatchFileState.Missing)
                {
                    outcome = PatchOutcome.Skipped;
                }

                return Result(fileName, outcome, status.Reason, status.Message, Array.Empty<string>());
            }
        }
    }

    private PatchFileResult RestoreFile(string fileName)
    {
        InstallState state = _store.Load();
        InstalledFile? installed = state.Get(fileName);
        if (installed is null || !_fileSystem.FileExists(_options.GetBackupPath(fileName, installed.OriginalSha256)))
        {
            return Result(fileName, PatchOutcome.Skipped, PatchReason.NoBackup, $"There is no backup of the original {fileName}.", Array.Empty<string>());
        }

        string path = _options.GetClientPath(fileName);
        if (_fileSystem.FileExists(path) && ContentHash.Matches(installed.OriginalSha256, _fileSystem.ReadAllBytes(path)))
        {
            return Result(fileName, PatchOutcome.Unchanged, PatchReason.AlreadyOriginal, $"{fileName} already is the original.", Array.Empty<string>());
        }

        RestoreBackup(fileName, installed, state);
        return Result(fileName, PatchOutcome.Restored, PatchReason.RestoreRequested, $"Original {fileName} restored.", Array.Empty<string>());
    }

    /// <summary>
    /// Icon.utx: the icon operations drawn on the original. The effect packages: <see cref="EffectComposer"/>, with
    /// the icons over the target read from the original Icon.utx (<see cref="ReadOriginal"/>).
    /// </summary>
    private (byte[] Bytes, IReadOnlyList<string> Warnings) BuildPatch(
        string fileName,
        byte[] original,
        IReadOnlyList<IPatchOperation> operations,
        IAssetStore assets,
        Manifest manifest)
    {
        if (ClientFiles.IsIconPackage(fileName))
        {
            var package = new IconPackage(Path.GetFileNameWithoutExtension(fileName), L2File.FromBytes(original));
            var iconOperations = new List<IIconOperation>();
            foreach (IPatchOperation operation in operations)
            {
                if (operation is not IIconOperation icon)
                {
                    throw new InvalidDataException($"'{operation.Describe()}' cannot be applied to {fileName}.");
                }

                iconOperations.Add(icon);
            }

            OperationBuildResult build = OperationComposer.Build(package, iconOperations, assets);
            return (build.Replacement.FileBytes, build.Warnings);
        }

        var effectOperations = new List<IEffectOperation>();
        foreach (IPatchOperation operation in operations)
        {
            if (operation is not IEffectOperation effect)
            {
                throw new InvalidDataException($"'{operation.Describe()}' cannot be applied to {fileName}.");
            }

            effectOperations.Add(effect);
        }

        EffectBuildResult effects = EffectComposer.Build(fileName, original, effectOperations, () =>
        {
            byte[]? icons = ReadOriginal(manifest, ClientFiles.IconUtx);
            if (icons is null)
            {
                return null;
            }

            return new IconPackage(Path.GetFileNameWithoutExtension(ClientFiles.IconUtx), L2File.FromBytes(icons));
        });
        return (effects.FileBytes, Array.Empty<string>());
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
            results.Add(Result(fileName, PatchOutcome.Blocked, PatchReason.GameRunning, message, Array.Empty<string>()));
        }

        return results;
    }

    private static PatchFileResult Result(string fileName, PatchOutcome outcome, PatchReason reason, string message, IReadOnlyList<string> warnings)
    {
        return new PatchFileResult(fileName, outcome, message, warnings)
        {
            Reason = reason,
        };
    }

    private static PatchFileResult Failed(string fileName, Exception exception)
    {
        return Result(fileName, PatchOutcome.Failed, PatchReason.Error, exception.Message, Array.Empty<string>());
    }

    private static bool IsExpectedFailure(Exception exception)
    {
        return exception is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException or FormatException;
    }

    private sealed record Plan(PendingAction Pending, PatchReason Reason, string Message);

    private sealed record Inspection(
        PatchFileStatus Status,
        byte[]? Current,
        IReadOnlyList<IPatchOperation> Operations,
        string RecipeKey,
        InstalledFile? Installed);
}
