using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CpLauncher.ViewModels;

namespace CpLauncher;

/// <summary>
/// <c>--smoke &lt;report&gt;</c>: drives the real view models of a started launcher (Apply, Restore original, a
/// preset and "restore my settings") and writes a key=value report. For the published-exe check against a fake
/// client; never point it at a real game folder.
/// </summary>
public static class SmokeRun
{
    public static async Task<string> RunAsync(MainWindowViewModel main, TimeSpan opened, TimeSpan ready)
    {
        var report = new StringBuilder();
        Add(report, "opened_ms", opened.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
        Add(report, "ready_ms", ready.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
        if (main.CurrentPage is not DashboardViewModel dashboard)
        {
            Add(report, "page", main.CurrentPage?.GetType().Name ?? "none");
            return report.ToString();
        }

        Add(report, "page", "dashboard");
        Add(report, "manifest", dashboard.ManifestVersionText + " / " + dashboard.ManifestOriginText);
        foreach (NoticeViewModel banner in dashboard.Banners)
        {
            Add(report, "banner", banner.Text);
        }

        Add(report, "packs", string.Join(",", dashboard.Packs.Select(pack => $"{pack.Id}{(pack.IsSelected ? "*" : string.Empty)}:{pack.Thumbnails.Count}")));
        Add(report, "presets", dashboard.Presets.Count.ToString(CultureInfo.InvariantCulture));
        Add(report, "announcements", dashboard.Announcements.Count.ToString(CultureInfo.InvariantCulture));
        Add(report, "update", dashboard.UpdateText ?? "none");
        AddIcon(report, "before", dashboard);

        await Run(report, "apply", dashboard, dashboard.ApplyCommand.CanExecute(null), () =>
        {
            return dashboard.ApplyCommand.ExecuteAsync(null);
        });
        AddIcon(report, "after_apply", dashboard);

        await Run(report, "restore", dashboard, dashboard.RestoreOriginalCommand.CanExecute(null), () =>
        {
            return dashboard.RestoreOriginalCommand.ExecuteAsync(null);
        });
        AddIcon(report, "after_restore", dashboard);

        PresetViewModel? preset = dashboard.Presets.FirstOrDefault();
        if (preset is not null)
        {
            await Run(report, "preset", dashboard, dashboard.ApplyPresetCommand.CanExecute(preset), () =>
            {
                return dashboard.ApplyPresetCommand.ExecuteAsync(preset);
            });
            await Run(report, "restore_settings", dashboard, dashboard.RestoreMySettingsCommand.CanExecute(null), () =>
            {
                return dashboard.RestoreMySettingsCommand.ExecuteAsync(null);
            });
        }

        foreach (string detail in dashboard.Details)
        {
            Add(report, "detail", detail);
        }

        return report.ToString();
    }

    /// <summary>
    /// <c>--smoke-update &lt;report&gt;</c>: reports the offered launcher update and clicks "Aktualizuj". The
    /// report is written before the click (the process quits right after a successful start of the hand-off)
    /// and again after it. True when the update was started and the launcher is closing.
    /// </summary>
    public static async Task<bool> RunUpdateAsync(MainWindowViewModel main, string reportPath)
    {
        var report = new StringBuilder();
        if (main.CurrentPage is not DashboardViewModel dashboard)
        {
            Add(report, "page", main.CurrentPage?.GetType().Name ?? "none");
            await File.WriteAllTextAsync(reportPath, report.ToString());
            return false;
        }

        Add(report, "version", dashboard.LauncherVersionText);
        Add(report, "manifest", dashboard.ManifestVersionText + " / " + dashboard.ManifestOriginText);
        foreach (NoticeViewModel banner in dashboard.Banners)
        {
            Add(report, "banner", banner.Text);
        }

        Add(report, "update", dashboard.UpdateText ?? "none");
        Add(report, "update_hint", dashboard.UpdateHint ?? "none");
        bool canUpdate = dashboard.UpdateLauncherCommand.CanExecute(null);
        Add(report, "update_enabled", canUpdate ? "yes" : "no");
        foreach (string detail in dashboard.Details)
        {
            Add(report, "detail", detail);
        }

        await File.WriteAllTextAsync(reportPath, report.ToString());
        if (!canUpdate)
        {
            return false;
        }

        // A started update shuts the launcher down right away, so this second write may not happen.
        await dashboard.UpdateLauncherCommand.ExecuteAsync(null);
        Add(report, "after_update", dashboard.LastAction?.Text ?? "no result");
        await File.WriteAllTextAsync(reportPath, report.ToString());
        return dashboard.IsBusy;
    }

    /// <summary>Renders the launcher window (only it, not the screen) into a PNG next to the report.</summary>
    public static async Task CaptureAsync(Window window, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
        }, DispatcherPriority.Background);
        Render(window, path);
        ScrollViewer? scroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll?.Content is Control content)
        {
            Render(content, Path.ChangeExtension(path, ".content.png"));
        }
    }

    private static void Render(Control control, string path)
    {
        var size = new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(control);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static async Task Run(StringBuilder report, string name, DashboardViewModel dashboard, bool canExecute, Func<Task> action)
    {
        if (!canExecute)
        {
            Add(report, name, "disabled");
            return;
        }

        await action();
        Add(report, name, dashboard.LastAction?.Text ?? "no result");
    }

    private static void AddIcon(StringBuilder report, string name, DashboardViewModel dashboard)
    {
        string hash = dashboard.IconStatus?.CurrentSha256 ?? "none";
        Add(report, name, $"{dashboard.IconStatus?.State} sha256={hash} pending={dashboard.IconStatus?.Pending} | {dashboard.IconState.Text}");
    }

    private static void Add(StringBuilder report, string key, string value)
    {
        report.Append(key).Append('=').Append(value.Replace('\n', ' ')).Append('\n');
    }
}
