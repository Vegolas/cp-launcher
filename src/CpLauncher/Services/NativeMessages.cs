using System.Runtime.InteropServices;

namespace CpLauncher.Services;

/// <summary>
/// A plain Windows message box, for the moments without an Avalonia window: another launcher already runs, or
/// the update hand-off failed and the member has to rename a file by hand.
/// </summary>
internal static partial class NativeMessages
{
    private const uint IconError = 0x10;
    private const uint IconInformation = 0x40;
    private const uint SetForeground = 0x10000;

    public static void ShowInformation(string caption, string text)
    {
        Show(caption, text, IconInformation);
    }

    public static void ShowError(string caption, string text)
    {
        Show(caption, text, IconError);
    }

    private static void Show(string caption, string text, uint icon)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"{caption}: {text}");
            return;
        }

        MessageBox(IntPtr.Zero, text, caption, icon | SetForeground);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}
