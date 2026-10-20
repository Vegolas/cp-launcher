using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CpLauncher.Views;

/// <summary>
/// The pictures of the pages (Assets/Art). Each one is optional: a missing file gives null and the page just has no
/// picture there, so a build without the art folder still works.
/// </summary>
public static class Art
{
    /// <summary>The CP guide next to the Icon.utx dialog on the dashboard.</summary>
    public static Bitmap? Guide { get; } = Load("guide.png");

    /// <summary>Hero background on the dashboard, already mirrored to face the guide.</summary>
    public static Bitmap? Dragon { get; } = Load("dragon.jpg");

    /// <summary>The settings page, already mirrored to face the form.</summary>
    public static Bitmap? Dwarf { get; } = Load("dwarf.png");

    /// <summary>The first-run page.</summary>
    public static Bitmap? Mechanic { get; } = Load("mechanic.png");

    private static Bitmap? Load(string fileName)
    {
        var uri = new Uri($"avares://CpLauncher/Assets/Art/{fileName}");
        if (!AssetLoader.Exists(uri))
        {
            return null;
        }

        using Stream stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }
}
