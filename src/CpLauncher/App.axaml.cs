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
    private readonly LauncherProcess? _process;

    /// <summary>For the XAML previewer; the launcher starts with <see cref="App(LauncherProcess)"/>.</summary>
    public App()
    {
    }

    public App(LauncherProcess process)
    {
        _process = process;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _process is not null)
        {
#if CPLAUNCHER_SIMULATE_STARTUP_FAILURE
            SimulateStartupFailure();
#endif
            LauncherProcess process = _process;
            CommandLineOptions options = process.Options;
            var dialogs = new WindowDialogs();
            var host = new LauncherHost(process.Updates, new DesktopLifetime(desktop), process.HttpClient, process.Log);
            LauncherStartup startup = LauncherBootstrap.Create(
                options,
                new PhysicalFileSystem(),
                new SystemProcessMonitor(),
                new ProcessGameStarter(),
                dialogs,
                Environment.GetEnvironmentVariable(LauncherPaths.DataDirectoryVariable),
                host);
            var viewModel = new MainWindowViewModel(startup.Services, startup.Notes);
            var window = new MainWindow { DataContext = viewModel };
            dialogs.Owner = window;
            window.Opened += async (_, _) =>
            {
                TimeSpan opened = DateTime.Now - Process.GetCurrentProcess().StartTime;
                await viewModel.StartAsync();
                process.CompleteStartup();
                if (options.SmokeReportPath is not null)
                {
                    TimeSpan ready = DateTime.Now - Process.GetCurrentProcess().StartTime;
                    await SmokeRun.CaptureAsync(window, options.SmokeReportPath + ".loaded.png");
                    string report = await SmokeRun.RunAsync(viewModel, opened, ready);
                    await SmokeRun.CaptureAsync(window, options.SmokeReportPath + ".done.png");
                    await File.WriteAllTextAsync(options.SmokeReportPath, report);
                    CloseLater(window);
                }
                else if (options.SmokeUpdateReportPath is not null)
                {
                    bool updating = await SmokeRun.RunUpdateAsync(viewModel, options.SmokeUpdateReportPath);
                    if (!updating)
                    {
                        CloseLater(window);
                    }
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CloseLater(MainWindow window)
    {
        Dispatcher.UIThread.Post(() =>
        {
            window.Close();
        }, DispatcherPriority.Background);
    }

#if CPLAUNCHER_SIMULATE_STARTUP_FAILURE
    /// <summary>Only in a test build (<c>-p:CpLauncherSimulateStartupFailure=true</c>): a version that breaks
    /// at startup, to check the automatic rollback after an update.</summary>
    private static void SimulateStartupFailure()
    {
        throw new InvalidOperationException("Simulated startup failure (test build with CpLauncherSimulateStartupFailure).");
    }
#endif

    /// <summary>Quits through Avalonia, after the current command has finished.</summary>
    private sealed class DesktopLifetime : ILauncherLifetime
    {
        private readonly IClassicDesktopStyleApplicationLifetime _desktop;

        public DesktopLifetime(IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
        }

        public void Shutdown()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _desktop.Shutdown();
            });
        }
    }
}
