using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpLauncher.Services;

namespace CpLauncher.ViewModels;

/// <summary>First run (and "Zmień folder klienta"): choose and validate the L2Dex client folder.</summary>
public partial class SetupViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Func<Task> _completed;
    private readonly Func<Task>? _cancelled;

    public SetupViewModel(LauncherServices services, Func<Task> completed, Func<Task>? cancelled)
    {
        _services = services;
        _completed = completed;
        _cancelled = cancelled;
        _clientDirectory = services.Settings.Current.ClientDirectory ?? string.Empty;
    }

    public ObservableCollection<string> Errors { get; } = new ObservableCollection<string>();

    public bool CanCancel
    {
        get
        {
            return _cancelled is not null;
        }
    }

    public string RequiredFilesText
    {
        get
        {
            return $"Folder musi zawierać {ClientFolder.IconRelativePath} i {ClientFolder.GameBinaryRelativePath}.";
        }
    }

    [ObservableProperty]
    private string _clientDirectory;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? folder = await _services.Dialogs.PickFolderAsync("Wybierz folder klienta L2Dex");
        if (folder is null)
        {
            return;
        }

        ClientDirectory = folder;
        Validate();
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        ClientFolderCheck check = Validate();
        if (!check.IsValid)
        {
            return;
        }

        try
        {
            _services.Settings.Update(settings => settings with { ClientDirectory = check.Directory });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Errors.Add($"Nie udało się zapisać ustawień: {exception.Message}");
            return;
        }

        await _completed();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (_cancelled is not null)
        {
            await _cancelled();
        }
    }

    partial void OnClientDirectoryChanged(string value)
    {
        Errors.Clear();
    }

    private ClientFolderCheck Validate()
    {
        ClientFolderCheck check = ClientFolder.Validate(_services.FileSystem, ClientDirectory);
        Errors.Clear();
        foreach (string error in check.Errors)
        {
            Errors.Add(error);
        }

        return check;
    }
}
