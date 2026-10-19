using L2Mod.Core.Imaging;

namespace L2Mod.Core.Badges;

/// <summary>
/// Badge size includes the border. Sizes that are multiples of 4 keep the badge on DXT1 block edges.
/// <see cref="Crop"/> &gt; 0 takes only the centre Crop x Crop pixels of the badge icon before shrinking it,
/// which drops background and makes the subject larger.
/// </summary>
public sealed record BadgeStyle(int Size, IconCorner Corner, int Border, int Crop = 0)
{
    public Rgb BorderColor { get; init; } = Rgb.Black;

    public static BadgeStyle Default
    {
        get
        {
            return new BadgeStyle(16, IconCorner.BottomRight, 1);
        }
    }
}

/// <summary>Draws a shrunken copy of one icon, with a border, into a corner of another icon.</summary>
public static class BadgeComposer
{
    public static PixelRect GetBadgeRect(int iconWidth, int iconHeight, BadgeStyle style)
    {
        return IconCorners.GetRect(iconWidth, iconHeight, style.Size, style.Size, style.Corner);
    }

    public static RgbaImage Compose(RgbaImage icon, RgbaImage badgeSource, BadgeStyle style)
    {
        RgbaImage result = icon.Clone();
        Draw(result, badgeSource, style);
        return result;
    }

    /// <summary>Draws the badge onto <paramref name="icon"/> in place and returns the rectangle it covers.</summary>
    public static PixelRect Draw(RgbaImage icon, RgbaImage badgeSource, BadgeStyle style)
    {
        if (style.Size < 4 || style.Size > Math.Min(icon.Width, icon.Height))
        {
            throw new ArgumentException($"Badge size {style.Size} must be between 4 and {Math.Min(icon.Width, icon.Height)}.");
        }

        if (style.Border < 0 || style.Border * 2 >= style.Size - 1)
        {
            throw new ArgumentException($"Border {style.Border} is too thick for a {style.Size}px badge.");
        }

        RgbaImage source = badgeSource;
        if (style.Crop > 0)
        {
            if (style.Crop > Math.Min(badgeSource.Width, badgeSource.Height))
            {
                throw new ArgumentException($"Crop {style.Crop} is larger than the {badgeSource.Width}x{badgeSource.Height} badge icon.");
            }

            int left = (badgeSource.Width - style.Crop) / 2;
            int top = (badgeSource.Height - style.Crop) / 2;
            source = badgeSource.Crop(new PixelRect(left, top, style.Crop, style.Crop));
        }

        PixelRect rect = GetBadgeRect(icon.Width, icon.Height, style);
        icon.FillRectangle(rect, style.BorderColor.Red, style.BorderColor.Green, style.BorderColor.Blue);
        int inner = style.Size - (style.Border * 2);
        RgbaImage badge = source.ResizeArea(inner, inner);
        icon.DrawImage(badge, rect.X + style.Border, rect.Y + style.Border);
        return rect;
    }
}
