using CpLauncher.Services;
using CpLauncher.ViewModels;

namespace CpLauncher.Tests;

public class NavigationTests
{
    private readonly LauncherFixture _fixture = new LauncherFixture();

    public NavigationTests()
    {
        _fixture.Publish(_fixture.Manifest());
    }

    [Fact]
    public async Task Start_WithoutClientFolder_ShowsTheSetup()
    {
        var main = new MainWindowViewModel(_fixture.CreateServices(), Array.Empty<string>());

        await main.StartAsync();

        SetupViewModel setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.False(setup.CanCancel);
        Assert.Equal(string.Empty, setup.ClientDirectory);
    }

    [Fact]
    public async Task Start_WithAValidClientFolder_ShowsTheDashboardWithStartupNotesOnce()
    {
        var main = new MainWindowViewModel(_fixture.CreateServices(_fixture.Settings), new[] { "Nieznany argument: --x" });

        await main.StartAsync();

        DashboardViewModel dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.Contains(dashboard.Banners, banner => banner.Text == "Nieznany argument: --x");
        await dashboard.ChangeClientFolderCommand.ExecuteAsync(null);
        SetupViewModel setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.True(setup.CanCancel);
        await setup.CancelCommand.ExecuteAsync(null);
        DashboardViewModel again = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.DoesNotContain(again.Banners, banner => banner.Text == "Nieznany argument: --x");
    }

    [Fact]
    public async Task Setup_RejectsAFolderThatIsNotAClient()
    {
        var main = new MainWindowViewModel(_fixture.CreateServices(), Array.Empty<string>());
        await main.StartAsync();
        var setup = (SetupViewModel)main.CurrentPage!;

        setup.ClientDirectory = @"C:\data";
        await setup.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(2, setup.Errors.Count);
        Assert.Same(setup, main.CurrentPage);
        setup.ClientDirectory = @"C:\dat";
        Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task Setup_BrowseAndContinue_SavesTheFolderAndOpensTheDashboard()
    {
        LauncherServices services = _fixture.CreateServices(new LauncherSettings { ManifestSource = LauncherFixture.SourceDirectory });
        var main = new MainWindowViewModel(services, Array.Empty<string>());
        await main.StartAsync();
        var setup = (SetupViewModel)main.CurrentPage!;
        _fixture.Dialogs.PickedFolder = @"C:\client\";

        await setup.BrowseCommand.ExecuteAsync(null);
        Assert.Empty(setup.Errors);
        await setup.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\client", services.Settings.Current.ClientDirectory);
        Assert.Equal(@"C:\client", new SettingsStore(_fixture.FileSystem, _fixture.Paths.SettingsPath).Load().Settings.ClientDirectory);
        DashboardViewModel dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.Equal(3, dashboard.Packs.Count);
    }

    [Fact]
    public async Task Setup_CancelledPicker_KeepsTheTypedFolder()
    {
        var main = new MainWindowViewModel(_fixture.CreateServices(), Array.Empty<string>());
        await main.StartAsync();
        var setup = (SetupViewModel)main.CurrentPage!;
        setup.ClientDirectory = @"C:\typed";
        _fixture.Dialogs.PickedFolder = null;

        await setup.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\typed", setup.ClientDirectory);
    }
}
