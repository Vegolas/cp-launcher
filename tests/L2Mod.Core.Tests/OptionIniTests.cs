using System.Text;
using L2Mod.Core.Ini;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;

namespace L2Mod.Core.Tests;

public class OptionIniTests
{
    /// <summary>Shaped like the client's Option.ini: CRLF, blank line between sections, trailing blank line.</summary>
    private const string Sample =
        "[Video]\r\nGamePlayViewportX=1573\r\nEffectDetail=2\r\nRefreshRate=50\r\n\r\n" +
        "[Game]\r\nIsLockShortcutWnd=False\r\nFileRegisterPath1=D:\r\n\r\n" +
        "[PotionsSettings]\r\nMP_ENABLED=1\r\n\r\n";

    private readonly InMemoryFileSystem _fileSystem = new InMemoryFileSystem();
    private readonly FakeProcessMonitor _processes = new FakeProcessMonitor();
    private readonly InstallerOptions _options = new InstallerOptions(@"C:\client", @"C:\data");
    private readonly OptionPresetInstaller _installer;

    public OptionIniTests()
    {
        _fileSystem.Seed(_options.OptionIniPath, Bytes(Sample));
        _installer = new OptionPresetInstaller(_options, _fileSystem, _processes);
    }

    [Fact]
    public void Merge_ReplacesAnExistingValueAndKeepsEveryOtherByte()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("Video", "EffectDetail", "0") });

        Assert.Equal(Sample.Replace("EffectDetail=2", "EffectDetail=0"), Text(result.Bytes));
        Assert.Equal(new[] { new IniValue("Video", "EffectDetail", "0") }, result.Changed);
    }

    [Fact]
    public void Merge_MatchesNamesCaseInsensitivelyAndKeepsTheFileSpelling()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("video", "effectdetail", "1") });

        Assert.Equal(Sample.Replace("EffectDetail=2", "EffectDetail=1"), Text(result.Bytes));
    }

    [Fact]
    public void Merge_AddsAMissingKeyAfterTheLastLineOfItsSection()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("Game", "ShowDropItem", "True") });

        Assert.Equal(Sample.Replace("FileRegisterPath1=D:\r\n", "FileRegisterPath1=D:\r\nShowDropItem=True\r\n"), Text(result.Bytes));
    }

    [Fact]
    public void Merge_DoesNotTouchTheSameKeyInAnotherSection()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("Game", "EffectDetail", "0") });

        Assert.Equal(Sample.Replace("FileRegisterPath1=D:\r\n", "FileRegisterPath1=D:\r\nEffectDetail=0\r\n"), Text(result.Bytes));
    }

    [Fact]
    public void Merge_AppendsAMissingSection()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("Audio", "SoundVolume", "0"), new IniValue("Audio", "MusicVolume", "0") });

        Assert.Equal(Sample + "[Audio]\r\nSoundVolume=0\r\nMusicVolume=0\r\n", Text(result.Bytes));
    }

    [Fact]
    public void Merge_KeepsLfLineEndingsAndAMissingFinalNewline()
    {
        const string Lf = "[Video]\nEffectDetail=2";

        IniMergeResult result = IniMerger.Merge(Bytes(Lf), new[] { new IniValue("Video", "EffectDetail", "0"), new IniValue("Video", "Gamma", "1") });

        Assert.Equal("[Video]\nEffectDetail=0\nGamma=1", Text(result.Bytes));
    }

    [Fact]
    public void Merge_ReportsNoChangesWhenValuesAlreadyMatch()
    {
        IniMergeResult result = IniMerger.Merge(Bytes(Sample), new[] { new IniValue("Video", "EffectDetail", "2") });

        Assert.False(result.HasChanges);
        Assert.Equal(Bytes(Sample), result.Bytes);
    }

    [Fact]
    public void Merge_PreservesNonAsciiBytes()
    {
        byte[] original = Bytes("[Game]\r\nFileRegisterPath1=D:\\gry\\");
        byte[] withLatin = original.Concat(new byte[] { 0xB3, 0xF3, 0x0D, 0x0A }).ToArray();

        IniMergeResult result = IniMerger.Merge(withLatin, new[] { new IniValue("Game", "IsLockShortcutWnd", "True") });

        Assert.Equal(withLatin.Concat(Bytes("IsLockShortcutWnd=True\r\n")).ToArray(), result.Bytes);
    }

    [Fact]
    public void Apply_BacksUpBeforeTheFirstChangeOnly()
    {
        PresetResult first = _installer.Apply(Preset("mass-pvp", new IniValue("Video", "EffectDetail", "0")));
        PresetResult second = _installer.Apply(Preset("quiet", new IniValue("Game", "IsLockShortcutWnd", "True")));

        Assert.Equal(PresetOutcome.Applied, first.Outcome);
        Assert.Equal(PresetOutcome.Applied, second.Outcome);
        Assert.Equal(Bytes(Sample), _fileSystem.ReadAllBytes(_options.OptionIniBackupPath));
        string current = Text(_fileSystem.ReadAllBytes(_options.OptionIniPath));
        Assert.Contains("EffectDetail=0\r\n", current);
        Assert.Contains("IsLockShortcutWnd=True\r\n", current);
    }

    [Fact]
    public void Apply_WithoutChangesWritesNothing()
    {
        PresetResult result = _installer.Apply(Preset("same", new IniValue("Video", "EffectDetail", "2")));

        Assert.Equal(PresetOutcome.Unchanged, result.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
        Assert.False(_installer.HasBackup);
    }

    [Fact]
    public void RestoreBackup_PutsTheUserSettingsBackAndForgetsTheBackup()
    {
        _installer.Apply(Preset("mass-pvp", new IniValue("Video", "EffectDetail", "0")));

        PresetResult result = _installer.RestoreBackup();

        Assert.Equal(PresetOutcome.Restored, result.Outcome);
        Assert.Equal(Bytes(Sample), _fileSystem.ReadAllBytes(_options.OptionIniPath));
        Assert.False(_installer.HasBackup);
        Assert.Equal(PresetOutcome.NoBackup, _installer.RestoreBackup().Outcome);
    }

    [Fact]
    public void ApplyAndRestore_AreBlockedWhileTheGameRuns()
    {
        _installer.Apply(Preset("mass-pvp", new IniValue("Video", "EffectDetail", "0")));
        _processes.Running.Add("L2.bin");
        _fileSystem.WrittenPaths.Clear();

        PresetResult apply = _installer.Apply(Preset("quiet", new IniValue("Game", "IsLockShortcutWnd", "True")));
        PresetResult restore = _installer.RestoreBackup();

        Assert.Equal(PresetOutcome.Blocked, apply.Outcome);
        Assert.Equal(PresetOutcome.Blocked, restore.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Fact]
    public void Apply_RefusesWindowPositionKeys()
    {
        Assert.Throws<ArgumentException>(() => _installer.Apply(Preset("bad", new IniValue("Video", "GamePlayViewportX", "0"))));
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    [Fact]
    public void Apply_ReportsAMissingOptionIni()
    {
        _fileSystem.DeleteFile(_options.OptionIniPath);

        PresetResult result = _installer.Apply(Preset("mass-pvp", new IniValue("Video", "EffectDetail", "0")));

        Assert.Equal(PresetOutcome.Failed, result.Outcome);
        Assert.Empty(_fileSystem.WrittenPaths);
    }

    private static OptionPreset Preset(string id, params IniValue[] values)
    {
        return new OptionPreset(id, id, values);
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.Latin1.GetBytes(text);
    }

    private static string Text(byte[] bytes)
    {
        return Encoding.Latin1.GetString(bytes);
    }
}
