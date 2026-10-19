using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CpLauncher.Services;
using CpLauncher.ViewModels;
using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Installation;
using L2Mod.Core.Signing;
using L2Mod.Core.Tests;

namespace CpLauncher.Tests;

/// <summary>
/// Manifests over the synthetic Icon.utx of <see cref="TestIcons"/>: badges (skill0365 ← skill1303), tags
/// (skill0102) and an overlay pack (skill1059, not selected by default), plus one preset.
/// </summary>
public static class TestManifests
{
    public static byte[] OverlayPng { get; } = PngCodec.Encode(TestPackageBuilder.SolidImage(8, 8, 250, 220, 0));

    public static string OverlayHash
    {
        get
        {
            return ContentHash.Sha256Hex(OverlayPng);
        }
    }

    public static string OverlayAssetPath
    {
        get
        {
            return $"assets/{OverlayHash}.png";
        }
    }

    public static JsonObject Create(string originalHash)
    {
        return new JsonObject
        {
            ["schema"] = 1,
            ["version"] = "2026.10.20.1",
            ["enabled"] = true,
            ["launcher"] = new JsonObject
            {
                ["version"] = "1.0.0",
                ["url"] = "launcher/CpLauncher-1.0.0.exe",
                ["sha256"] = new string('b', 64),
            },
            ["supportedOriginals"] = new JsonObject { ["Icon.utx"] = new JsonArray(originalHash) },
            ["announcements"] = new JsonArray(
                new JsonObject { ["date"] = "2026-10-18", ["title"] = "Starsze", ["text"] = "Pierwsze ogłoszenie." },
                new JsonObject { ["date"] = "2026-10-20", ["title"] = "Start", ["text"] = "<b>Zwykły</b> tekst." }),
            ["packs"] = new JsonArray(
                Pack("badges", "Znaczki tańców i pieśni", true, new JsonObject
                {
                    ["op"] = "badge", ["file"] = "Icon.utx", ["target"] = "skill0365", ["badge"] = "skill1303", ["size"] = 20, ["corner"] = "br",
                }),
                Pack("tags", "Tagi debuffów", true, new JsonObject
                {
                    ["op"] = "tag", ["file"] = "Icon.utx", ["target"] = "skill0102", ["color"] = "#E02020", ["size"] = 8, ["corner"] = "tl",
                }),
                Pack("marks", "Oznaczenia przedmiotów", false, new JsonObject
                {
                    ["op"] = "overlay", ["file"] = "Icon.utx", ["target"] = "skill1059", ["image"] = OverlayAssetPath, ["imageSha256"] = OverlayHash, ["corner"] = "br",
                })),
            ["optionPresets"] = new JsonArray(new JsonObject
            {
                ["id"] = "mass-pvp",
                ["name"] = "Masowe PvP",
                ["values"] = new JsonObject { ["Video"] = new JsonObject { ["EffectDetail"] = "0" } },
            }),
        };
    }

    public static byte[] Serialize(JsonObject manifest)
    {
        return Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    private static JsonObject Pack(string id, string name, bool isDefault, JsonObject operation)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["default"] = isDefault,
            ["operations"] = new JsonArray(operation),
        };
    }
}

/// <summary>
/// A client in C:\client (synthetic Icon.utx, L2.exe, L2Updater.exe, Option.ini), launcher data in C:\data and a
/// folder source in C:\source, all in memory, plus a throwaway signing key generated per test.
/// </summary>
public sealed class LauncherFixture
{
    public const string ClientDirectory = @"C:\client";
    public const string DataDirectory = @"C:\data";
    public const string SourceDirectory = @"C:\source";
    public const string OriginalOptionIni = "[Video]\r\nEffectDetail=2\r\nTextureDetail=1\r\n";

    public LauncherFixture()
    {
        FileSystem.Seed(IconPath, Original);
        FileSystem.Seed(Path.Combine(ClientDirectory, "system_season", "L2.exe"), new byte[] { 0x4D, 0x5A });
        FileSystem.Seed(Path.Combine(ClientDirectory, "L2Updater.exe"), new byte[] { 0x4D, 0x5A });
        FileSystem.Seed(OptionIniPath, Encoding.ASCII.GetBytes(OriginalOptionIni));
    }

    public InMemoryFileSystem FileSystem { get; } = new InMemoryFileSystem();

    public FakeProcessMonitor Processes { get; } = new FakeProcessMonitor();

    public FakeDialogs Dialogs { get; } = new FakeDialogs();

    public FakeGameStarter Game { get; } = new FakeGameStarter();

    public SigningKeyPair Key { get; } = ManifestSignature.GenerateKeyPair();

    public byte[] Original { get; } = TestIcons.BuildIconUtx();

    public LauncherPaths Paths { get; } = new LauncherPaths(DataDirectory);

    public string OriginalHash
    {
        get
        {
            return ContentHash.Sha256Hex(Original);
        }
    }

    public InstallerOptions InstallerOptions
    {
        get
        {
            return Paths.GetInstallerOptions(ClientDirectory);
        }
    }

    public string IconPath
    {
        get
        {
            return Path.Combine(ClientDirectory, "SysTextures", "Icon.utx");
        }
    }

    public string OptionIniPath
    {
        get
        {
            return Path.Combine(ClientDirectory, "system_season", "Option.ini");
        }
    }

    public LauncherSettings Settings
    {
        get
        {
            return new LauncherSettings { ClientDirectory = ClientDirectory, ManifestSource = SourceDirectory };
        }
    }

    public JsonObject Manifest()
    {
        return TestManifests.Create(OriginalHash);
    }

    /// <summary>Puts a signed manifest (and the overlay asset) into the folder source.</summary>
    public void Publish(JsonObject manifest, string? privateKeyPem = null)
    {
        byte[] bytes = TestManifests.Serialize(manifest);
        PublishBytes(bytes, ManifestSignature.Sign(bytes, privateKeyPem ?? Key.PrivateKeyPem));
    }

    public void PublishBytes(byte[] manifest, byte[] signature)
    {
        FileSystem.Seed(Path.Combine(SourceDirectory, "manifest.json"), manifest);
        FileSystem.Seed(Path.Combine(SourceDirectory, "manifest.json.sig"), signature);
        FileSystem.Seed(Path.Combine(SourceDirectory, "assets", $"{TestManifests.OverlayHash}.png"), TestManifests.OverlayPng);
    }

    public void TakeSourceOffline()
    {
        FileSystem.DeleteFile(Path.Combine(SourceDirectory, "manifest.json"));
        FileSystem.DeleteFile(Path.Combine(SourceDirectory, "manifest.json.sig"));
        FileSystem.DeleteFile(Path.Combine(SourceDirectory, "assets", $"{TestManifests.OverlayHash}.png"));
    }

    public IReadOnlyList<TrustedKey> TrustedKeys(bool development = false)
    {
        return new[] { new TrustedKey("test", Key.PublicKeyPem, development) };
    }

    public LauncherServices CreateServices(LauncherSettings? settings = null, bool developmentKey = false)
    {
        if (settings is not null)
        {
            new SettingsStore(FileSystem, Paths.SettingsPath).Save(settings);
        }

        var sources = new ManifestSourceFactory(FileSystem, new HttpClient(new StubHttpHandler()));
        return new LauncherServices(FileSystem, Processes, Paths, TrustedKeys(developmentKey), sources, Game, Dialogs)
        {
            CurrentVersion = new Version(1, 0, 0),
        };
    }

    public async Task<DashboardViewModel> CreateDashboardAsync(LauncherSettings? settings = null, bool developmentKey = false)
    {
        LauncherServices services = CreateServices(settings ?? Settings, developmentKey);
        var dashboard = new DashboardViewModel(services, () =>
        {
            return Task.CompletedTask;
        }, Array.Empty<string>());
        await dashboard.InitializeAsync();
        return dashboard;
    }

    public byte[] ReadIcon()
    {
        return FileSystem.ReadAllBytes(IconPath);
    }

    public string ReadOptionIni()
    {
        return Encoding.ASCII.GetString(FileSystem.ReadAllBytes(OptionIniPath));
    }
}

public sealed class FakeDialogs : IUserDialogs
{
    public bool ConfirmAnswer { get; set; }

    public string? PickedFolder { get; set; }

    public List<string> Confirmations { get; } = new List<string>();

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PickFolderAsync(string title)
    {
        return Task.FromResult(PickedFolder);
    }
}

public sealed class FakeGameStarter : IGameStarter
{
    public List<(string Path, string WorkingDirectory)> Started { get; } = new List<(string Path, string WorkingDirectory)>();

    public Exception? Failure { get; set; }

    public void Start(string executablePath, string workingDirectory)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Started.Add((executablePath, workingDirectory));
    }
}

/// <summary>Answers requests from a dictionary of paths; anything else is a connection failure.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

    public void Add(string absolutePath, byte[] body)
    {
        Responses[absolutePath] = () =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.RequestUri is not null && Responses.TryGetValue(request.RequestUri.AbsolutePath, out Func<HttpResponseMessage>? response))
        {
            return Task.FromResult(response());
        }

        throw new HttpRequestException("No such host is known.");
    }
}
