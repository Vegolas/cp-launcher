using System.Runtime.InteropServices;

namespace CpLauncher.Views;

/// <summary>
/// Whether the launcher may animate: off when Windows has "Animation effects" switched off (the reduced-motion
/// setting). Read on every use, so a change applies without a restart.
/// </summary>
internal static partial class Motion
{
    private const uint GetClientAreaAnimation = 0x1042;

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            if (!SystemParametersInfo(GetClientAreaAnimation, 0, out int enabled, 0))
            {
                return true;
            }

            return enabled != 0;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);
}
