using L2Mod.Core.Imaging;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Operations;

/// <summary>A solid colour square in a corner, e.g. a debuff category tag.</summary>
public sealed record TagOperation(string File, string Target, Rgb Color, int Size, IconCorner Corner) : IIconOperation
{
    public const int DefaultSize = 8;
    public const IconCorner DefaultCorner = IconCorner.TopLeft;

    public string Kind
    {
        get
        {
            return OperationKinds.Tag;
        }
    }

    public PixelRect? Apply(RgbaImage icon, IOperationSources sources)
    {
        return TagComposer.Draw(icon, Color, Size, Corner);
    }

    public string Describe()
    {
        return $"tag {File}:{Target} {Color} {Size}px {IconCorners.ToCode(Corner)}";
    }
}

/// <summary>
/// Draws a tag: the colour snapped to RGB565, plus a 1 px black edge on the two sides that face the rest
/// of the icon. With a size that is a multiple of 4 the tag covers whole DXT1 blocks and every block holds
/// at most two exact endpoint colours, so it encodes losslessly.
/// </summary>
public static class TagComposer
{
    public static PixelRect Draw(RgbaImage icon, Rgb color, int size, IconCorner corner)
    {
        if (size < 4 || size % 4 != 0 || size > Math.Min(icon.Width, icon.Height))
        {
            throw new ArgumentException($"Tag size {size} must be a multiple of 4 between 4 and {Math.Min(icon.Width, icon.Height)}.");
        }

        Rgb fill = Dxt1Codec.QuantizeToRgb565(color);
        PixelRect rect = IconCorners.GetRect(icon.Width, icon.Height, size, size, corner);
        icon.FillRectangle(rect, fill.Red, fill.Green, fill.Blue);

        int edgeX = rect.Right - 1;
        if (IconCorners.IsRight(corner))
        {
            edgeX = rect.X;
        }

        int edgeY = rect.Bottom - 1;
        if (IconCorners.IsBottom(corner))
        {
            edgeY = rect.Y;
        }

        icon.FillRectangle(new PixelRect(edgeX, rect.Y, 1, size), 0, 0, 0);
        icon.FillRectangle(new PixelRect(rect.X, edgeY, size, 1), 0, 0, 0);
        return rect;
    }
}
