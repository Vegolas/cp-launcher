using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Textures;

namespace AvaloniaSpike;

public partial class PackItem : ObservableObject
{
    public PackItem(string name, bool enabled)
    {
        Name = name;
        _isEnabled = enabled;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabled;
}

public partial class MainViewModel : ObservableObject
{
    private const string DefaultIconPath = @"C:\Lineage2Dex\.modding\work\original\Icon.utx";

    public MainViewModel(string? iconPath)
    {
        Packs = new ObservableCollection<PackItem>
        {
            new PackItem("Znaczki tańców i pieśni", true),
            new PackItem("Tagi debuffów", true),
            new PackItem("Oznaczenia przedmiotów", false),
        };
        LoadIcon(iconPath ?? DefaultIconPath);
    }

    public ObservableCollection<PackItem> Packs { get; }

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private string _iconStatus = "not loaded";

    [ObservableProperty]
    private string _status = "Gotowy.";

    [RelayCommand]
    private void Apply()
    {
        int enabled = Packs.Count(pack => pack.IsEnabled);
        Status = $"Zastosowano {enabled} paczek (spike – nic nie zapisano).";
    }

    private void LoadIcon(string path)
    {
        if (!File.Exists(path))
        {
            IconStatus = "missing";
            return;
        }

        try
        {
            var package = new IconPackage(path);
            RgbaImage image = package.Decode(package.FindTexture("skill0365")).UpscaleNearest(4);
            Icon = ToBitmap(image);
            IconStatus = "ok";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
        {
            IconStatus = $"error: {exception.Message}";
        }
    }

    private static Bitmap ToBitmap(RgbaImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            int rowBytes = image.Width * 4;
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(image.Pixels, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
            }
        }

        return bitmap;
    }
}
