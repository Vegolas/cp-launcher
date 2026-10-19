using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CpLauncher.Services;
using CpLauncher.ViewModels;
using CpLauncher.Views;
using L2Mod.Core.Installation;

namespace CpLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CommandLineOptions options = CommandLineOptions.Parse(desktop.Args ?? Array.Empty<string>());
            var dialogs = new WindowDialogs();
            LauncherStartup startup = LauncherBootstrap.Create(
                options,
                new PhysicalFileSystem(),
                new SystemProcessMonitor(),
                new ProcessGameStarter(),
                dialogs,
                Environment.GetEnvironmentVariable(LauncherPaths.DataDirectoryVariable));
            var viewModel = new MainWindowViewModel(startup.Services, startup.Notes);
            var window = new MainWindow { DataContext = viewModel };
            dialogs.Owner = window;
            window.Opened += async (_, _) =>
            {
                TimeSpan opened = DateTime.Now - Process.GetCurrentProcess().StartTime;
                await viewModel.StartAsync();
                if (options.SmokeReportPath is not null)
                {
                    TimeSpan ready = DateTime.Now - Process.GetCurrentProcess().StartTime;
                    await SmokeRun.CaptureAsync(window, options.SmokeReportPath + ".loaded.png");
                    string report = await SmokeRun.RunAsync(viewModel, opened, ready);
                    await SmokeRun.CaptureAsync(window, options.SmokeReportPath + ".done.png");
                    await File.WriteAllTextAsync(options.SmokeReportPath, report);
                    Dispatcher.UIThread.Post(() =>
                    {
                        window.Close();
                    }, DispatcherPriority.Background);
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
