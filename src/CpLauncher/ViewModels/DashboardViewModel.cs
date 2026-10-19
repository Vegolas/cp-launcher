using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpLauncher.Services;
using L2Mod.Core;
using L2Mod.Core.Ini;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Updates;

namespace CpLauncher.ViewModels;

/// <summary>
/// The main screen: manifest and Icon.utx status, packs with previews, Apply / Restore original, Option.ini
/// presets, announcements, "Uruchom L2Dex" and the launcher update. All file changes go through Core's
/// <see cref="PatchInstaller"/> and <see cref="OptionPresetInstaller"/>, which also enforce the game-running
/// guard; the launcher update goes through Core's <see cref="UpdateService"/>.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Func<Task> _changeClientFolder;
    private readonly IReadOnlyList<string> _startupNotes;
    private readonly string _clientDirectory;
    private readonly InstallerOptions _installerOptions;
    private readonly PatchInstaller _installer;
    private readonly OptionPresetInstaller _presetInstaller;
    private readonly StatusText? _restartNotice;
    private LoadedManifest? _manifest;
    private IManifestSource? _source;
    private PatchFileStatus? _iconStatus;
    private int _statusGeneration;
    private PendingUpdate? _pendingUpdate;
    private bool _shuttingDown;

    public DashboardViewModel(LauncherServices services, Func<Task> changeClientFolder, IReadOnlyList<string> startupNotes)
    {
        string? clientDirectory = services.Settings.Current.ClientDirectory;
        if (string.IsNullOrWhiteSpace(clientDirectory))
        {
            throw new ArgumentException("The client folder is not set.", nameof(services));
        }

        _services = services;
        _changeClientFolder = changeClientFolder;
        _startupNotes = startupNotes;
        _clientDirectory = clientDirectory;
        _installerOptions = services.Paths.GetInstallerOptions(clientDirectory);
        _installer = new PatchInstaller(_installerOptions, services.FileSystem, services.Processes);
        _presetInstaller = new OptionPresetInstaller(_installerOptions, services.FileSystem, services.Processes);
        UpdateStartupInfo? restart = services.Updates.TakeRestartInfo();
        if (restart is not null)
        {
            _restartNotice = StatusTexts.DescribeRestart(restart, services.CurrentVersion, services.Log.Path);
        }

        _manifestSourceText = services.Settings.Current.ManifestSource ?? string.Empty;
        GameExecutableOptions = BuildGameExecutableOptions(services.Settings.Current.GameExecutable);
        _selectedGameExecutable = GameExecutableOptions.First(option => option.RelativePath == services.Settings.Current.GameExecutable);
    }

    public string ClientDirectory
    {
        get
        {
            return _clientDirectory;
        }
    }

    public string LauncherVersionText
    {
        get
        {
            return $"wersja {_services.CurrentVersion}";
        }
    }

    public ObservableCollection<NoticeViewModel> Banners { get; } = new ObservableCollection<NoticeViewModel>();

    public ObservableCollection<PackViewModel> Packs { get; } = new ObservableCollection<PackViewModel>();

    public ObservableCollection<PresetViewModel> Presets { get; } = new ObservableCollection<PresetViewModel>();

    public ObservableCollection<AnnouncementViewModel> Announcements { get; } = new ObservableCollection<AnnouncementViewModel>();

    /// <summary>Technical notes (Core's English messages, skipped packs, build warnings).</summary>
    public ObservableCollection<string> Details { get; } = new ObservableCollection<string>();

    public IReadOnlyList<GameExecutableOption> GameExecutableOptions { get; }

    /// <summary>The manifest in use (null until loaded or when there is none).</summary>
    public Manifest? Manifest
    {
        get
        {
            return _manifest?.Manifest;
        }
    }

    public PatchFileStatus? IconStatus
    {
        get
        {
            return _iconStatus;
        }
    }

    [ObservableProperty]
    private string _manifestVersionText = "–";

    [ObservableProperty]
    private string _manifestOriginText = string.Empty;

    [ObservableProperty]
    private string _manifestSourceDescription = "–";

    [ObservableProperty]
    private NoticeViewModel _iconState = new NoticeViewModel(StatusSeverity.Info, "Sprawdzanie…");

    [ObservableProperty]
    private string? _pendingText;

    [ObservableProperty]
    private NoticeViewModel? _lastAction;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ApplyCommand), nameof(RestoreOriginalCommand), nameof(ApplyPresetCommand),
        nameof(RestoreMySettingsCommand), nameof(LaunchGameCommand), nameof(ChangeClientFolderCommand), nameof(SaveSettingsCommand),
        nameof(UpdateLauncherCommand))]
    private bool _isBusy;

    /// <summary>"Dostępna nowa wersja launchera: …" while the manifest offers a newer launcher.</summary>
    [ObservableProperty]
    private string? _updateText;

    /// <summary>Why "Aktualizuj" cannot be used, or how the update is going.</summary>
    [ObservableProperty]
    private string? _updateHint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _packsEditable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreMySettingsCommand))]
    private bool _hasSettingsBackup;

    [ObservableProperty]
    private bool _hasPresets;

    [ObservableProperty]
    private bool _hasAnnouncements;

    [ObservableProperty]
    private string _manifestSourceText;

    [ObservableProperty]
    private GameExecutableOption _selectedGameExecutable;

    [ObservableProperty]
    private string? _settingsMessage;

    public Task InitializeAsync()
    {
        return RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(LoadAsync);
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        await RunBusyAsync(() =>
        {
            return ApplyCoreAsync(false);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRestoreOriginal))]
    private async Task RestoreOriginalAsync()
    {
        await RunBusyAsync(RestoreCoreAsync);
    }

    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private async Task ApplyPresetAsync(PresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (!_services.FileSystem.FileExists(_installerOptions.OptionIniPath))
            {
                LastAction = new NoticeViewModel(StatusSeverity.Warning, "Nie znaleziono system_season\\Option.ini – uruchom grę raz, żeby go utworzyła.");
                return;
            }

            PresetResult result = await Task.Run(() =>
            {
                return _presetInstaller.Apply(preset.Preset);
            });
            ShowPresetResult(result, preset.Name);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRestoreMySettings))]
    private async Task RestoreMySettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            PresetResult result = await Task.Run(_presetInstaller.RestoreBackup);
            ShowPresetResult(result, string.Empty);
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void LaunchGame()
    {
        string relative = _services.Settings.Current.GameExecutable;
        string? path = GameExecutables.Resolve(_clientDirectory, relative);
        if (path is null)
        {
            LastAction = new NoticeViewModel(StatusSeverity.Error, $"„{relative}” nie jest programem w folderze klienta. Wybierz inny w ustawieniach launchera.");
            return;
        }

        if (!_services.FileSystem.FileExists(path))
        {
            LastAction = new NoticeViewModel(StatusSeverity.Error, $"Nie znaleziono {relative} w folderze klienta. Wybierz inny program w ustawieniach launchera.");
            return;
        }

        try
        {
            _services.GameStarter.Start(path, Path.GetDirectoryName(path) ?? _clientDirectory);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            LastAction = new NoticeViewModel(StatusSeverity.Error, $"Nie udało się uruchomić {relative}: {exception.Message}");
            return;
        }

        string text = $"Uruchomiono {relative}.";
        if (_iconStatus?.Pending is PendingAction.Install or PendingAction.Rebuild)
        {
            text += " Uwaga: wybrane zmiany nie zostały zastosowane.";
        }

        LastAction = new NoticeViewModel(StatusSeverity.Success, text);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChangeClientFolderAsync()
    {
        await _changeClientFolder();
    }

    /// <summary>
    /// Downloads the offered launcher, verifies it against the signed manifest, starts it as the hand-off and
    /// quits, so the hand-off can put it in place of this exe and start it. Settings are saved on every change,
    /// so nothing is pending here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLauncher))]
    private async Task UpdateLauncherAsync()
    {
        PendingUpdate? update = _pendingUpdate;
        if (update is null)
        {
            return;
        }

        await RunBusyAsync(() =>
        {
            return InstallUpdateAsync(update);
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SaveSettingsAsync()
    {
        string? source = null;
        if (!string.IsNullOrWhiteSpace(ManifestSourceText))
        {
            source = ManifestSourceText.Trim();
        }

        try
        {
            _services.Sources.Create(source);
        }
        catch (FormatException exception)
        {
            SettingsMessage = exception.Message;
            return;
        }

        LauncherSettings current = _services.Settings.Current;
        bool sourceChanged = !string.Equals(source, current.ManifestSource, StringComparison.Ordinal);
        try
        {
            _services.Settings.Update(settings => settings with { ManifestSource = source, GameExecutable = SelectedGameExecutable.RelativePath });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = $"Nie udało się zapisać ustawień: {exception.Message}";
            return;
        }

        SettingsMessage = "Zapisano ustawienia.";
        if (sourceChanged)
        {
            await RefreshAsync();
        }
    }

    private bool IsIdle()
    {
        return !IsBusy;
    }

    private bool CanUpdateLauncher()
    {
        return !IsBusy && _pendingUpdate is not null;
    }

    private bool CanApply()
    {
        return !IsBusy && _manifest is not null && PacksEditable && _iconStatus is not null && _iconStatus.Pending != PendingAction.None;
    }

    private bool CanRestoreOriginal()
    {
        return !IsBusy && _iconStatus is not null && _iconStatus.HasBackup && _iconStatus.State != PatchFileState.Original;
    }

    private bool CanApplyPreset(PresetViewModel? preset)
    {
        return !IsBusy && preset is not null;
    }

    private bool CanRestoreMySettings()
    {
        return !IsBusy && HasSettingsBackup;
    }

    partial void OnIsBusyChanged(bool value)
    {
        UpdatePackEditability();
    }

    partial void OnPacksEditableChanged(bool value)
    {
        UpdatePackEditability();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            // Last line of defence of the UI: report instead of crashing the launcher.
            LastAction = new NoticeViewModel(StatusSeverity.Error, $"Nieoczekiwany błąd: {exception.Message}");
            Details.Add(exception.ToString());
        }
        finally
        {
            // Once the hand-off runs, this launcher only waits to be closed.
            IsBusy = _shuttingDown;
        }
    }

    private async Task LoadAsync()
    {
        Banners.Clear();
        Details.Clear();
        foreach (string note in _startupNotes)
        {
            Banners.Add(new NoticeViewModel(StatusSeverity.Warning, note));
        }

        if (_restartNotice is not null)
        {
            Banners.Add(new NoticeViewModel(_restartNotice.Severity, _restartNotice.Text));
        }

        ManifestLoadResult result;
        try
        {
            _source = _services.Sources.Create(_services.Settings.Current.EffectiveManifestSource);
            result = await _services.Manifests.LoadAsync(_source, CancellationToken.None);
        }
        catch (FormatException exception)
        {
            _source = null;
            result = new ManifestLoadResult(_services.Manifests.LoadCached(), exception.Message);
        }

        ManifestSourceDescription = _source?.Description ?? "nie ustawiono";
        _manifest = result.Manifest;
        if (result.Problem is not null)
        {
            StatusSeverity severity = result.Manifest is null ? StatusSeverity.Error : StatusSeverity.Warning;
            string text = result.Problem;
            if (result.Manifest is not null)
            {
                text += $" Używam ostatniego sprawdzonego manifestu ({result.Manifest.Manifest.Version}).";
            }

            Banners.Add(new NoticeViewModel(severity, text));
        }

        ShowManifest(result.Manifest);
        await RefreshStatusAsync();
        await RenderPreviewsAsync();
        RefreshPresetState();
        if (_manifest is not null && !_manifest.Manifest.Enabled && _iconStatus?.Pending == PendingAction.Restore)
        {
            await ApplyCoreAsync(true);
        }
    }

    private void ShowManifest(LoadedManifest? loaded)
    {
        Packs.Clear();
        Presets.Clear();
        Announcements.Clear();
        UpdateText = null;
        UpdateHint = null;
        _pendingUpdate = null;
        UpdateLauncherCommand.NotifyCanExecuteChanged();
        if (loaded is null)
        {
            ManifestVersionText = "brak";
            ManifestOriginText = string.Empty;
            PacksEditable = false;
            HasPresets = false;
            HasAnnouncements = false;
            return;
        }

        Manifest manifest = loaded.Manifest;
        ManifestVersionText = string.IsNullOrEmpty(manifest.Version) ? "nieznana" : manifest.Version;
        ManifestOriginText = loaded.Origin == ManifestOrigin.Source ? "pobrany teraz" : "zapisana kopia";
        if (loaded.Key.IsDevelopment)
        {
            Banners.Add(new NoticeViewModel(StatusSeverity.Warning, "Manifest podpisany kluczem deweloperskim (--dev-public-key) – tylko do testów."));
        }

        bool readable = manifest.Schema == ManifestParser.SupportedSchema;
        if (!readable)
        {
            Banners.Add(new NoticeViewModel(StatusSeverity.Error, "Nowe przepisy wymagają nowszej wersji launchera. Zainstalowane zmiany zostają bez zmian do czasu aktualizacji."));
        }

        if (!manifest.Enabled)
        {
            Banners.Add(new NoticeViewModel(StatusSeverity.Warning, "Lider CP wyłączył zmiany (wyłącznik awaryjny). Launcher przywraca oryginalny Icon.utx."));
        }

        foreach (ManifestNote note in loaded.Notes)
        {
            Details.Add($"{StatusTexts.DescribeNote(note)} ({note.Message})");
        }

        if (loaded.Notes.Count > 0)
        {
            int count = loaded.Notes.Count;
            string text = $"Launcher pominął część przepisów ({count} {Polish.Plural(count, "uwaga", "uwagi", "uwag")}) – szczegóły na dole strony.";
            if (count == 1)
            {
                text = $"Launcher pominął część przepisów. {StatusTexts.DescribeNote(loaded.Notes[0])}";
            }

            Banners.Add(new NoticeViewModel(StatusSeverity.Info, text));
        }

        ShowUpdate(loaded);
        PacksEditable = readable && manifest.Enabled;
        LauncherSettings settings = _services.Settings.Current;
        foreach (Pack pack in manifest.Packs)
        {
            Packs.Add(new PackViewModel(pack, settings.IsSelected(pack), OnPackSelectionChanged));
        }

        UpdatePackEditability();
        foreach (OptionPreset preset in manifest.OptionPresets)
        {
            Presets.Add(new PresetViewModel(preset));
        }

        foreach (Announcement announcement in manifest.Announcements.OrderByDescending(item => item.Date))
        {
            Announcements.Add(new AnnouncementViewModel(announcement));
        }

        HasPresets = Presets.Count > 0;
        HasAnnouncements = Announcements.Count > 0;
    }

    /// <summary>
    /// Offers a newer launcher from the manifest. It can be downloaded only from the source the manifest came
    /// from just now, and only over https (http on loopback for local tests): <c>launcher.url</c> is relative
    /// to that source. A version that failed or was rolled back on this computer is not offered.
    /// </summary>
    private void ShowUpdate(LoadedManifest loaded)
    {
        LauncherUpdates updates = _services.Updates;
        LauncherVersion? skipped = _services.Settings.Current.SkippedVersion;
        UpdateCheckResult check = updates.Create(null).Check(loaded.Manifest, skipped);
        if (check.Status == UpdateCheckStatus.Skipped)
        {
            Details.Add($"Wersja {check.Available} launchera jest pominięta na tym komputerze (nie zainstalowała się albo została wycofana).");
        }
        else if (check.Status == UpdateCheckStatus.InvalidRelease)
        {
            Details.Add(check.Message);
        }

        if (!check.IsUpdateAvailable)
        {
            return;
        }

        UpdateText = $"Dostępna nowa wersja launchera: {check.Available}";
        if (loaded.Origin != ManifestOrigin.Source)
        {
            UpdateHint = "Połącz się z internetem i kliknij „Odśwież”, żeby pobrać aktualizację.";
            return;
        }

        Uri? releaseBase = LauncherUpdates.GetReleaseBase(_source);
        if (releaseBase is null)
        {
            UpdateHint = "Aktualizację launchera można pobrać tylko ze źródła https://.";
            return;
        }

        UpdateService service;
        try
        {
            service = updates.Create(releaseBase);
        }
        catch (ArgumentException exception)
        {
            UpdateHint = "Aktualizację launchera można pobrać tylko ze źródła https://.";
            Details.Add(exception.Message);
            return;
        }

        UpdateCheckResult downloadable = service.Check(loaded.Manifest, skipped);
        if (!downloadable.IsUpdateAvailable)
        {
            UpdateHint = "Przepisy podają niepoprawny adres nowej wersji launchera.";
            Details.Add(downloadable.Message);
            return;
        }

        _pendingUpdate = new PendingUpdate(service, downloadable);
        UpdateLauncherCommand.NotifyCanExecuteChanged();
    }

    private async Task InstallUpdateAsync(PendingUpdate update)
    {
        UpdateService service = update.Service;
        LauncherVersion version = update.Check.Available!;
        _services.Log.Write($"Update to {version}: downloading {update.Check.Release?.Url} from {service.Options.ReleaseBaseUri}.");
        UpdateHint = "Pobieranie aktualizacji…";
        await _services.Updates.WaitForCleanupAsync();
        bool downloading = true;
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (downloading)
            {
                UpdateHint = StatusTexts.DescribeDownload(value);
            }
        });

        DownloadedUpdate downloaded;
        try
        {
            downloaded = await service.DownloadAndVerifyAsync(update.Check, progress, CancellationToken.None);
        }
        catch (UpdateException exception)
        {
            downloading = false;
            FailUpdate("Nie udało się pobrać aktualizacji launchera – spróbuj ponownie później (szczegóły na dole).", exception);
            return;
        }

        downloading = false;
        _services.Log.Write($"Update to {version}: {downloaded.Length} bytes verified (SHA-256 {downloaded.Sha256}).");
        UpdateHint = "Uruchamianie nowej wersji…";
        try
        {
            await service.ApplyAndRestartAsync(downloaded);
        }
        catch (UpdateException exception)
        {
            FailUpdate("Pobrana wersja launchera nie uruchomiła się – nic nie zmieniono. Spróbuj ponownie (szczegóły na dole).", exception);
            return;
        }

        _services.Log.Write($"Update to {version}: hand-off started, closing.");
        _shuttingDown = true;
        UpdateHint = null;
        LastAction = new NoticeViewModel(StatusSeverity.Success, $"Instaluję wersję {version} launchera – za chwilę uruchomi się ponownie.");
        _services.Lifetime.Shutdown();
    }

    private void FailUpdate(string text, UpdateException exception)
    {
        _services.Log.Write("Update failed: " + exception.Message);
        UpdateHint = null;
        LastAction = new NoticeViewModel(StatusSeverity.Error, text);
        Details.Add(exception.Message);
    }

    private async Task ApplyCoreAsync(bool automatic)
    {
        if (_manifest is null)
        {
            return;
        }

        if (GameProcesses.GetBlockingMessage(_services.Processes) is not null)
        {
            string text = StatusTexts.GameRunning;
            if (automatic)
            {
                text = "Lider CP wyłączył zmiany, ale gra jest uruchomiona. Zamknij ją i kliknij „Odśwież”, żeby przywrócić oryginał.";
            }

            LastAction = new NoticeViewModel(StatusSeverity.Warning, text);
            return;
        }

        Manifest manifest = _manifest.Manifest;
        IReadOnlyList<string> selected = SelectedPackIds();
        if (manifest.Enabled)
        {
            AssetSyncResult assets = await _services.Assets.EnsureAsync(AssetCache.GetOverlays(manifest, selected), _source, CancellationToken.None);
            if (!assets.IsComplete)
            {
                foreach (string item in assets.Missing.Concat(assets.Errors))
                {
                    Details.Add(item);
                }

                LastAction = new NoticeViewModel(StatusSeverity.Error, "Brakuje obrazków do wybranych paczek – połącz się z internetem i spróbuj ponownie (szczegóły na dole).");
                return;
            }
        }

        int operationCount = manifest.GetOperations(ClientFiles.IconUtx, selected).Count;
        IReadOnlyList<PatchFileResult> results = await Task.Run(() =>
        {
            return _installer.Apply(manifest, selected, _services.Assets.Store);
        });
        PatchFileResult result = results.Single(item => item.FileName == ClientFiles.IconUtx);
        StatusText described = StatusTexts.DescribeApply(result, operationCount);
        LastAction = new NoticeViewModel(described.Severity, described.Text);
        AddDetails(result);
        if (!automatic && result.Outcome is PatchOutcome.Installed or PatchOutcome.Rebuilt or PatchOutcome.Restored or PatchOutcome.Unchanged)
        {
            try
            {
                _services.Settings.Update(settings => settings.WithSelection(manifest, selected));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Details.Add($"settings.json: {exception.Message}");
                LastAction = new NoticeViewModel(StatusSeverity.Warning, described.Text + " Nie udało się zapamiętać wyboru paczek.");
            }
        }

        await RefreshStatusAsync();
    }

    private async Task RestoreCoreAsync()
    {
        if (_iconStatus?.State == PatchFileState.Unknown)
        {
            bool confirmed = await _services.Dialogs.ConfirmAsync(
                "Przywrócić oryginał?",
                "Icon.utx w kliencie to wersja, której launcher nie zna – możliwe, że L2Dex właśnie go zaktualizował. "
                + "Przywrócenie zapisanej kopii może zastąpić nowszy plik od L2Dex starszą wersją.\n\nPrzywrócić mimo to?",
                "Przywróć");
            if (!confirmed)
            {
                LastAction = new NoticeViewModel(StatusSeverity.Info, "Anulowano – Icon.utx bez zmian.");
                return;
            }
        }

        IReadOnlyList<PatchFileResult> results = await Task.Run(_installer.Restore);
        PatchFileResult result = results.Single(item => item.FileName == ClientFiles.IconUtx);
        StatusText described = StatusTexts.DescribeRestore(result);
        LastAction = new NoticeViewModel(described.Severity, described.Text);
        AddDetails(result);
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        int generation = ++_statusGeneration;
        Manifest manifest = _manifest?.Manifest ?? EmptyManifest;
        IReadOnlyList<string> selected = SelectedPackIds();
        StatusSnapshot snapshot = await Task.Run(() =>
        {
            return ReadStatus(manifest, selected);
        });
        if (generation != _statusGeneration)
        {
            return;
        }

        _iconStatus = snapshot.Status;
        if (snapshot.Status is null)
        {
            IconState = new NoticeViewModel(StatusSeverity.Error, $"Nie można sprawdzić Icon.utx: {snapshot.Error}");
            PendingText = null;
        }
        else if (_manifest is null && snapshot.Status.State == PatchFileState.Unknown)
        {
            IconState = new NoticeViewModel(StatusSeverity.Info, "Bez przepisów nie da się sprawdzić wersji Icon.utx.");
            PendingText = null;
        }
        else
        {
            StatusText state = StatusTexts.DescribeState(snapshot.Status);
            IconState = new NoticeViewModel(state.Severity, state.Text);
            PendingText = null;
            if (_manifest is not null)
            {
                PendingText = StatusTexts.DescribePending(snapshot.Status);
            }
        }

        OnPropertyChanged(nameof(IconStatus));
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreOriginalCommand.NotifyCanExecuteChanged();
    }

    private StatusSnapshot ReadStatus(Manifest manifest, IReadOnlyList<string> selected)
    {
        try
        {
            PatchFileStatus status = _installer.GetStatus(manifest, selected).Single(item => item.FileName == ClientFiles.IconUtx);
            return new StatusSnapshot(status, null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new StatusSnapshot(null, exception.Message);
        }
    }

    private async Task RenderPreviewsAsync()
    {
        if (_manifest is null || Packs.Count == 0)
        {
            return;
        }

        Manifest manifest = _manifest.Manifest;
        List<string> allPacks = manifest.Packs.Select(pack => pack.Id).ToList();
        AssetSyncResult assets = await _services.Assets.EnsureAsync(AssetCache.GetOverlays(manifest, allPacks), _source, CancellationToken.None);
        foreach (string error in assets.Errors)
        {
            Details.Add(error);
        }

        IReadOnlyList<PackPreview> previews = await Task.Run(() =>
        {
            byte[]? original;
            try
            {
                original = _installer.ReadOriginal(manifest, ClientFiles.IconUtx);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // The status already reports a damaged state.json or an unreadable file.
                original = null;
            }

            if (original is null)
            {
                return Array.Empty<PackPreview>();
            }

            return _services.Previews.Render(original, manifest, _services.Assets.Store);
        });
        foreach (PackPreview preview in previews)
        {
            PackViewModel? pack = Packs.FirstOrDefault(item => item.Id == preview.PackId);
            if (pack is not null)
            {
                pack.SetPreview(preview);
            }
        }
    }

    private void RefreshPresetState()
    {
        HasSettingsBackup = _presetInstaller.HasBackup;
        byte[]? ini = null;
        try
        {
            if (_services.FileSystem.FileExists(_installerOptions.OptionIniPath))
            {
                ini = _services.FileSystem.ReadAllBytes(_installerOptions.OptionIniPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Details.Add($"Option.ini: {exception.Message}");
        }

        foreach (PresetViewModel preset in Presets)
        {
            preset.IsApplied = ini is not null && !IniMerger.Merge(ini, preset.Preset.Values).HasChanges;
        }
    }

    private void ShowPresetResult(PresetResult result, string presetName)
    {
        StatusText described = StatusTexts.DescribePreset(result, presetName);
        LastAction = new NoticeViewModel(described.Severity, described.Text);
        if (result.Outcome == PresetOutcome.Failed)
        {
            Details.Add(result.Message);
        }

        RefreshPresetState();
    }

    private void OnPackSelectionChanged()
    {
        _ = RefreshStatusAfterToggleAsync();
    }

    private async Task RefreshStatusAfterToggleAsync()
    {
        try
        {
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            Details.Add(exception.ToString());
        }
    }

    private void UpdatePackEditability()
    {
        foreach (PackViewModel pack in Packs)
        {
            pack.IsEditable = PacksEditable && !IsBusy;
        }
    }

    private void AddDetails(PatchFileResult result)
    {
        if (result.Outcome == PatchOutcome.Failed)
        {
            Details.Add(result.Message);
        }

        foreach (string warning in result.Warnings)
        {
            Details.Add(warning);
        }
    }

    private IReadOnlyList<string> SelectedPackIds()
    {
        return Packs.Where(pack => pack.IsSelected).Select(pack => pack.Id).ToList();
    }

    private static IReadOnlyList<GameExecutableOption> BuildGameExecutableOptions(string configured)
    {
        var options = new List<GameExecutableOption>(GameExecutables.Options);
        if (!options.Any(option => option.RelativePath == configured))
        {
            options.Add(new GameExecutableOption(configured, configured));
        }

        return options;
    }

    /// <summary>Stands in for a missing manifest when only the file state is shown (nothing is applied with it).</summary>
    private static Manifest EmptyManifest
    {
        get
        {
            return new Manifest(
                ManifestParser.SupportedSchema,
                string.Empty,
                true,
                null,
                new Dictionary<string, IReadOnlyList<string>>(),
                Array.Empty<Announcement>(),
                Array.Empty<Pack>(),
                Array.Empty<OptionPreset>());
        }
    }

    private sealed record StatusSnapshot(PatchFileStatus? Status, string? Error);

    /// <summary>An update the member can install now: the updater for the manifest's source and its check.</summary>
    private sealed record PendingUpdate(UpdateService Service, UpdateCheckResult Check);
}
