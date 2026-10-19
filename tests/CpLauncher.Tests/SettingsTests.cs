using System.Text;
using CpLauncher.Services;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

public class SettingsTests
{
    private const string SettingsPath = @"C:\data\settings.json";

    private readonly InMemoryFileSystem _fileSystem = new InMemoryFileSystem();

    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        SettingsLoadResult result = new SettingsStore(_fileSystem, SettingsPath).Load();

        Assert.Null(result.Problem);
        Assert.Null(result.Settings.ClientDirectory);
        Assert.Equal(GameExecutables.Default, result.Settings.GameExecutable);
        Assert.Empty(result.Settings.PackSelection);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new SettingsStore(_fileSystem, SettingsPath);
        var settings = new LauncherSettings
        {
            ClientDirectory = @"C:\Lineage2Dex",
            ManifestSource = "https://example.org/cp/",
            GameExecutable = GameExecutables.DexLauncher,
            PackSelection = new Dictionary<string, bool> { ["badges"] = true, ["tags"] = false },
        };

        store.Save(settings);
        LauncherSettings loaded = store.Load().Settings;

        Assert.Equal(settings.ClientDirectory, loaded.ClientDirectory);
        Assert.Equal(settings.ManifestSource, loaded.ManifestSource);
        Assert.Equal(settings.GameExecutable, loaded.GameExecutable);
        Assert.Equal(settings.PackSelection, loaded.PackSelection);
        string json = Encoding.UTF8.GetString(_fileSystem.ReadAllBytes(SettingsPath));
        Assert.Contains("\"clientDirectory\"", json);
        Assert.DoesNotContain("effectiveManifestSource", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Load_DamagedFile_FallsBackToDefaultsWithAProblem(string content)
    {
        _fileSystem.Seed(SettingsPath, Encoding.UTF8.GetBytes(content));

        SettingsLoadResult result = new SettingsStore(_fileSystem, SettingsPath).Load();

        Assert.NotNull(result.Problem);
        Assert.Null(result.Settings.ClientDirectory);
    }

    [Fact]
    public void Load_MissingFields_GetDefaults()
    {
        _fileSystem.Seed(SettingsPath, Encoding.UTF8.GetBytes("{ \"clientDirectory\": \"C:\\\\x\", \"gameExecutable\": \"\", \"packSelection\": null }"));

        LauncherSettings settings = new SettingsStore(_fileSystem, SettingsPath).Load().Settings;

        Assert.Equal(@"C:\x", settings.ClientDirectory);
        Assert.Equal(GameExecutables.Default, settings.GameExecutable);
        Assert.NotNull(settings.PackSelection);
    }

    [Fact]
    public void PackSelection_UsesSavedChoicesAndDefaultsForNewPacks()
    {
        Manifest manifest = Parse();
        var settings = new LauncherSettings { PackSelection = new Dictionary<string, bool> { ["badges"] = false, ["marks"] = true } };

        Assert.Equal(new[] { "tags", "marks" }, settings.GetSelectedPackIds(manifest));
        Assert.Equal(new[] { "badges", "tags" }, LauncherSettings.Default.GetSelectedPackIds(manifest));
    }

    [Fact]
    public void WithSelection_RecordsEveryPackAndKeepsChoicesForUnlistedPacks()
    {
        Manifest manifest = Parse();
        var settings = new LauncherSettings { PackSelection = new Dictionary<string, bool> { ["old-pack"] = true } };

        LauncherSettings updated = settings.WithSelection(manifest, new[] { "tags" });

        Assert.False(updated.PackSelection["badges"]);
        Assert.True(updated.PackSelection["tags"]);
        Assert.False(updated.PackSelection["marks"]);
        Assert.True(updated.PackSelection["old-pack"]);
        Assert.Single(settings.PackSelection);
    }

    [Fact]
    public void EffectiveManifestSource_FallsBackToTheBuiltInDefault()
    {
        Assert.Equal(LauncherDefaults.ManifestSource, LauncherSettings.Default.EffectiveManifestSource);
        Assert.Equal("https://x/", new LauncherSettings { ManifestSource = "https://x/" }.EffectiveManifestSource);
    }

    [Fact]
    public void LauncherPaths_PrefersCommandLineThenEnvironmentThenLocalAppData()
    {
        Assert.Equal(@"C:\cli", LauncherPaths.Resolve(@"C:\cli", @"C:\env").DataDirectory);
        Assert.Equal(@"C:\env", LauncherPaths.Resolve(null, @"C:\env").DataDirectory);
        Assert.Equal(InstallerOptions.DefaultDataDirectory, LauncherPaths.Resolve(" ", null).DataDirectory);
        Assert.EndsWith(Path.Combine("CpLauncher"), InstallerOptions.DefaultDataDirectory);
    }

    [Fact]
    public void LauncherPaths_KeepsCacheAndCoreDataTogether()
    {
        var paths = new LauncherPaths(@"C:\data");

        Assert.Equal(@"C:\data\settings.json", paths.SettingsPath);
        Assert.Equal(@"C:\data\cache\manifest.json", paths.CachedManifestPath);
        Assert.Equal(@"C:\data\cache\manifest.json.sig", paths.CachedSignaturePath);
        Assert.Equal(@"C:\data\cache\assets\abc.png", paths.GetCachedAssetPath("assets/abc.png"));
        InstallerOptions options = paths.GetInstallerOptions(@"C:\client");
        Assert.Equal(@"C:\data\state.json", options.StatePath);
        Assert.Equal(@"C:\data\backups", options.BackupDirectory);
    }

    [Fact]
    public void CommandLine_ParsesSwitchesAndReportsUnknownOnes()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--data", @"C:\d", "--SOURCE", "https://x/", "--bogus", "--smoke" });

        Assert.Equal(@"C:\d", options.DataDirectory);
        Assert.Equal("https://x/", options.ManifestSource);
        Assert.Null(options.ClientDirectory);
        Assert.Null(options.SmokeReportPath);
        Assert.Equal(2, options.Problems.Count);
        Assert.Contains(options.Problems, problem => problem.Contains("--bogus"));
        Assert.Contains(options.Problems, problem => problem.Contains("--smoke"));
    }

    [Fact]
    public void CommandLine_SkipsUpdateOptionsAndForwardsDataAndDevelopmentKey()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[]
        {
            "--updated-from", "1.0.0", "--data", @"C:\d", "--dev-public-key", @"C:\k\dev.pem", "--source", "https://x/", "--smoke-update", @"C:\r.txt",
        });

        Assert.Empty(options.Problems);
        Assert.Equal(@"C:\r.txt", options.SmokeUpdateReportPath);
        Assert.Equal(new[] { "--data", @"C:\d", "--dev-public-key", @"C:\k\dev.pem" }, options.ForwardedArguments);
        Assert.Equal(new[] { "--data", Path.GetFullPath("relative-data") }, CommandLineOptions.Parse(new[] { "--data", "relative-data" }).ForwardedArguments);
        Assert.Empty(CommandLineOptions.Parse(new[] { "--source", "https://x/", "--smoke", "r.txt" }).ForwardedArguments);
    }

    [Fact]
    public void SkippedLauncherVersion_RoundTripsAndIgnoresGarbage()
    {
        var fixture = new LauncherFixture();
        var store = new SettingsStore(fixture.FileSystem, fixture.Paths.SettingsPath);

        store.Save(LauncherSettings.Default with { SkippedLauncherVersion = "1.0.1" });

        Assert.Equal(L2Mod.Core.Updates.LauncherVersion.Parse("1.0.1"), store.Load().Settings.SkippedVersion);
        Assert.Null((LauncherSettings.Default with { SkippedLauncherVersion = "01.0.1" }).SkippedVersion);
        Assert.Null(LauncherSettings.Default.SkippedVersion);
    }

    [Fact]
    public void Log_AppendsLinesAndStartsANewFileAboveTheLimit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cplauncher-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new LauncherLog(Path.Combine(directory, "launcher.log"));
            log.Write("first");
            File.AppendAllText(log.Path!, new string('x', (int)LauncherLog.MaxBytes));
            log.Write("second");
            LauncherLog.None.Write("nowhere");

            Assert.Contains("first", File.ReadAllText(log.Path! + ".1"));
            string current = File.ReadAllText(log.Path!);
            Assert.Contains($"[{Environment.ProcessId}] second", current);
            Assert.DoesNotContain("first", current);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Bootstrap_AppliesOverridesAndTrustsTheDevelopmentKey()
    {
        var fixture = new LauncherFixture();
        fixture.FileSystem.Seed(@"C:\keys\dev.pem", Encoding.UTF8.GetBytes(fixture.Key.PublicKeyPem));
        CommandLineOptions options = CommandLineOptions.Parse(new[]
        {
            "--data", @"C:\data", "--client", @"C:\client", "--source", @"C:\source", "--dev-public-key", @"C:\keys\dev.pem",
        });

        LauncherStartup startup = LauncherBootstrap.Create(options, fixture.FileSystem, fixture.Processes, fixture.Game, fixture.Dialogs, null, fixture.CreateHost());

        Assert.Empty(startup.Notes);
        Assert.Equal(@"C:\client", startup.Services.Settings.Current.ClientDirectory);
        Assert.Equal(@"C:\source", startup.Services.Settings.Current.ManifestSource);
        Assert.True(fixture.FileSystem.FileExists(@"C:\data\settings.json"));
        Assert.Equal(2, startup.Services.TrustedKeys.Count);
        Assert.False(startup.Services.TrustedKeys[0].IsDevelopment);
        Assert.True(startup.Services.TrustedKeys[1].IsDevelopment);
    }

    [Fact]
    public void Bootstrap_ReportsAnUnusableDevelopmentKey()
    {
        var fixture = new LauncherFixture();
        fixture.FileSystem.Seed(@"C:\keys\dev.pem", Encoding.UTF8.GetBytes("not a key"));
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--data", @"C:\data", "--dev-public-key", @"C:\keys\dev.pem" });

        LauncherStartup startup = LauncherBootstrap.Create(options, fixture.FileSystem, fixture.Processes, fixture.Game, fixture.Dialogs, null, fixture.CreateHost());

        Assert.Single(startup.Services.TrustedKeys);
        Assert.Contains(startup.Notes, note => note.Contains("dev.pem"));
    }

    private static Manifest Parse()
    {
        var fixture = new LauncherFixture();
        return ManifestParser.Parse(TestManifests.Serialize(fixture.Manifest())).Manifest;
    }
}
