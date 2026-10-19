using CommunityToolkit.Mvvm.ComponentModel;
using CpLauncher.Services;

namespace CpLauncher.ViewModels;

/// <summary>Shows the setup page until a valid client folder is stored, then the dashboard.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private IReadOnlyList<string> _startupNotes;

    public MainWindowViewModel(LauncherServices services, IReadOnlyList<string> startupNotes)
    {
        _services = services;
        _startupNotes = startupNotes;
    }

    [ObservableProperty]
    private ObservableObject? _currentPage;

    public async Task StartAsync()
    {
        ClientFolderCheck check = ClientFolder.Validate(_services.FileSystem, _services.Settings.Current.ClientDirectory);
        if (check.IsValid)
        {
            await ShowDashboardAsync();
        }
        else
        {
            ShowSetup(false);
        }
    }

    private void ShowSetup(bool cancellable)
    {
        Func<Task>? cancel = null;
        if (cancellable)
        {
            cancel = ShowDashboardAsync;
        }

        CurrentPage = new SetupViewModel(_services, ShowDashboardAsync, cancel);
    }

    private async Task ShowDashboardAsync()
    {
        IReadOnlyList<string> notes = _startupNotes;
        _startupNotes = Array.Empty<string>();
        var dashboard = new DashboardViewModel(_services, ChangeClientFolderAsync, notes);
        CurrentPage = dashboard;
        await dashboard.InitializeAsync();
    }

    private Task ChangeClientFolderAsync()
    {
        ShowSetup(true);
        return Task.CompletedTask;
    }
}
