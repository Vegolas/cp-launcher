using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using L2Mod.Core.Imaging;

namespace CpLauncher.Views;

/// <summary>Core's RGBA image to an Avalonia bitmap, so view models stay free of UI types.</summary>
public sealed class RgbaImageConverter : IValueConverter
{
    public static readonly RgbaImageConverter Instance = new RgbaImageConverter();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RgbaImage image)
        {
            return null;
        }

        return ToBitmap(image);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public static WriteableBitmap ToBitmap(RgbaImage image)
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
