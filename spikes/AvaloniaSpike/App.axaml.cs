using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AvaloniaSpike;

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
            string[] args = desktop.Args ?? Array.Empty<string>();
            var viewModel = new MainViewModel(GetOption(args, "--icon"));
            var window = new MainWindow { DataContext = viewModel };
            string? smokeFile = GetOption(args, "--smoke");
            if (smokeFile is not null)
            {
                window.Opened += (_, _) =>
                {
                    TimeSpan elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
                    string report = $"opened_ms={elapsed.TotalMilliseconds:F0}\nicon={viewModel.IconStatus}\npacks={viewModel.Packs.Count}\n";
                    File.WriteAllText(smokeFile, report);
                    Dispatcher.UIThread.Post(() =>
                    {
                        window.Close();
                    }, DispatcherPriority.Background);
                };
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
