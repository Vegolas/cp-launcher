using L2Mod.Core.Ini;
using L2Mod.Core.Manifests;

namespace L2Mod.Core.Installation;

public enum PresetOutcome
{
    Applied,
    Unchanged,
    Restored,
    NoBackup,
    Blocked,
    Failed,
}

public sealed record PresetResult(PresetOutcome Outcome, string Message, IReadOnlyList<IniValue> Changed);

/// <summary>
/// Applies option presets to the client's Option.ini. The file is backed up before the first change and the
/// backup is kept until "restore my settings", which puts it back and deletes it (so the next preset backs up
/// the settings as they are then). Nothing is written while the game runs: it rewrites Option.ini on exit.
/// </summary>
public sealed class OptionPresetInstaller
{
    private readonly InstallerOptions _options;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessMonitor _processes;

    public OptionPresetInstaller(InstallerOptions options, IFileSystem fileSystem, IProcessMonitor processes)
    {
        _options = options;
        _fileSystem = fileSystem;
        _processes = processes;
    }

    public bool HasBackup
    {
        get
        {
            return _fileSystem.FileExists(_options.OptionIniBackupPath);
        }
    }

    public PresetResult Apply(OptionPreset preset)
    {
        foreach (IniValue value in preset.Values)
        {
            if (value.Key.StartsWith(ManifestParser.ForbiddenPresetKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Preset '{preset.Id}' sets {value.Section}.{value.Key}; window position keys are not allowed.");
            }
        }

        string? blocked = GameProcesses.GetBlockingMessage(_processes);
        if (blocked is not null)
        {
            return new PresetResult(PresetOutcome.Blocked, blocked, Array.Empty<IniValue>());
        }

        try
        {
            if (!_fileSystem.FileExists(_options.OptionIniPath))
            {
                return new PresetResult(PresetOutcome.Failed, "Option.ini was not found; start the game once first.", Array.Empty<IniValue>());
            }

            byte[] current = _fileSystem.ReadAllBytes(_options.OptionIniPath);
            IniMergeResult merge = IniMerger.Merge(current, preset.Values);
            if (!merge.HasChanges)
            {
                return new PresetResult(PresetOutcome.Unchanged, $"Option.ini already uses the '{preset.Name}' settings.", Array.Empty<IniValue>());
            }

            if (!HasBackup)
            {
                VerifiedWrite.Write(_fileSystem, _options.OptionIniBackupPath, current);
            }

            VerifiedWrite.Write(_fileSystem, _options.OptionIniPath, merge.Bytes);
            return new PresetResult(PresetOutcome.Applied, $"Preset '{preset.Name}' applied ({merge.Changed.Count} settings changed).", merge.Changed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PresetResult(PresetOutcome.Failed, exception.Message, Array.Empty<IniValue>());
        }
    }

    /// <summary>"Restore my settings": the Option.ini from before the first preset.</summary>
    public PresetResult RestoreBackup()
    {
        string? blocked = GameProcesses.GetBlockingMessage(_processes);
        if (blocked is not null)
        {
            return new PresetResult(PresetOutcome.Blocked, blocked, Array.Empty<IniValue>());
        }

        if (!HasBackup)
        {
            return new PresetResult(PresetOutcome.NoBackup, "There is no backup of Option.ini.", Array.Empty<IniValue>());
        }

        try
        {
            VerifiedWrite.Write(_fileSystem, _options.OptionIniPath, _fileSystem.ReadAllBytes(_options.OptionIniBackupPath));
            _fileSystem.DeleteFile(_options.OptionIniBackupPath);
            return new PresetResult(PresetOutcome.Restored, "Your Option.ini settings were restored.", Array.Empty<IniValue>());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PresetResult(PresetOutcome.Failed, exception.Message, Array.Empty<IniValue>());
        }
    }
}
