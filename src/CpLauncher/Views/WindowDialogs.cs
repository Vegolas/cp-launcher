using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CpLauncher.Services;

namespace CpLauncher.Views;

/// <summary><see cref="IUserDialogs"/> on top of the main window.</summary>
public sealed class WindowDialogs : IUserDialogs
{
    public Window? Owner { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (Owner is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, confirmText);
        bool result = await dialog.ShowDialog<bool>(Owner);
        return result;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is null)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };
        IReadOnlyList<IStorageFolder> folders = await Owner.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count == 0)
        {
            return null;
        }

        return folders[0].TryGetLocalPath();
    }
}

/// <summary>A yes/no question; closes with true only on the confirm button.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var confirm = new Button { Content = confirmText };
        confirm.Classes.Add("accent");
        confirm.Click += (_, _) =>
        {
            Close(true);
        };

        var cancel = new Button { Content = "Anuluj", IsDefault = true };
        cancel.Click += (_, _) =>
        {
            Close(false);
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
        };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(buttons);
        Content = content;
    }
}
