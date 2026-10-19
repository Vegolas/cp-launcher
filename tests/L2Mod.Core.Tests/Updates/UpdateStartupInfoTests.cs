using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests.Updates;

public class UpdateStartupInfoTests
{
    [Fact]
    public void Parse_NormalStart()
    {
        UpdateStartupInfo info = UpdateStartupInfo.Parse(Array.Empty<string>());

        Assert.Equal(UpdateStartupInfo.None, info);
        Assert.False(info.IsRestart);
        Assert.False(info.IsHandOff);
        Assert.Equal(TimeSpan.Zero, info.InstanceLockTimeout);
    }

    [Fact]
    public void Parse_HandOffOfAnUpdate()
    {
        UpdateStartupInfo info = UpdateStartupInfo.Parse(new[] { "--install-update", "1.0.0" });

        Assert.Equal(LauncherVersion.Parse("1.0.0"), info.InstallUpdateOver);
        Assert.True(info.IsHandOff);
        Assert.True(info.IsRestart);
        Assert.Equal(UpdateStartupInfo.RestartLockTimeout, info.InstanceLockTimeout);
    }

    [Fact]
    public void Parse_HandOffOfARollback()
    {
        UpdateStartupInfo info = UpdateStartupInfo.Parse(new[] { "--install-rollback", "1.0.1" });

        Assert.Equal(LauncherVersion.Parse("1.0.1"), info.InstallRollbackOver);
        Assert.True(info.IsHandOff);
    }

    [Theory]
    [InlineData("--updated-from")]
    [InlineData("--rolled-back-from")]
    [InlineData("--update-failed")]
    public void Parse_RestartsThatShowTheUi(string option)
    {
        UpdateStartupInfo info = UpdateStartupInfo.Parse(new[] { "--verbose", option, "1.0.1", "extra" });

        Assert.False(info.IsHandOff);
        Assert.True(info.IsRestart);
        Assert.Equal(UpdateStartupInfo.RestartLockTimeout, info.InstanceLockTimeout);
        LauncherVersion expected = LauncherVersion.Parse("1.0.1");
        Assert.Contains(expected, new[] { info.UpdatedFrom, info.RolledBackFrom, info.FailedVersion });
    }

    [Theory]
    [InlineData("--updated-from")]
    [InlineData("--updated-from", "latest")]
    [InlineData("--updated-from", "")]
    [InlineData("--UPDATED-FROM", "1.0.0")]
    [InlineData("1.0.0", "--updated-from")]
    [InlineData("--install-update", "v1.0.0")]
    public void Parse_IgnoresMalformedOptions(params string[] arguments)
    {
        UpdateStartupInfo info = UpdateStartupInfo.Parse(arguments);

        Assert.Equal(UpdateStartupInfo.None, info);
        Assert.False(info.IsRestart);
    }

    [Theory]
    [InlineData(@"C:\Games\CpLauncher.exe", @"C:\Games\CpLauncher.exe")]
    [InlineData(@"C:\Games\CpLauncher.exe.new", @"C:\Games\CpLauncher.exe")]
    [InlineData(@"C:\Games\CpLauncher.exe.OLD", @"C:\Games\CpLauncher.exe")]
    [InlineData(@"C:\Games\CpLauncher.exe.tmp", @"C:\Games\CpLauncher.exe.tmp")]
    public void GetLauncherPath_StripsTheHandOffSuffix(string processPath, string expected)
    {
        Assert.Equal(expected, UpdateOptions.GetLauncherPath(processPath));
    }
}
