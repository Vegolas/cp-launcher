namespace L2Mod.Core.Imaging;

public enum IconCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>Corner codes ("br", "bl", "tr", "tl") and placement of a rectangle in a corner of an icon.</summary>
public static class IconCorners
{
    public static IconCorner Parse(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "br":
                return IconCorner.BottomRight;
            case "bl":
                return IconCorner.BottomLeft;
            case "tr":
                return IconCorner.TopRight;
            case "tl":
                return IconCorner.TopLeft;
            default:
                throw new ArgumentException($"Unknown corner '{value}' (use br, bl, tr or tl).");
        }
    }

    public static string ToCode(IconCorner corner)
    {
        switch (corner)
        {
            case IconCorner.BottomRight:
                return "br";
            case IconCorner.BottomLeft:
                return "bl";
            case IconCorner.TopRight:
                return "tr";
            default:
                return "tl";
        }
    }

    public static bool IsRight(IconCorner corner)
    {
        return corner == IconCorner.BottomRight || corner == IconCorner.TopRight;
    }

    public static bool IsBottom(IconCorner corner)
    {
        return corner == IconCorner.BottomRight || corner == IconCorner.BottomLeft;
    }

    /// <summary>A width x height rectangle flush with the given corner of an icon.</summary>
    public static PixelRect GetRect(int iconWidth, int iconHeight, int width, int height, IconCorner corner)
    {
        int x = 0;
        int y = 0;
        if (IsRight(corner))
        {
            x = iconWidth - width;
        }

        if (IsBottom(corner))
        {
            y = iconHeight - height;
        }

        return new PixelRect(x, y, width, height);
    }
}
