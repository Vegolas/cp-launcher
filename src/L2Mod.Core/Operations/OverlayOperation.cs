using L2Mod.Core.Imaging;

namespace L2Mod.Core.Operations;

/// <summary>A small PNG with alpha (an asset referenced by path and SHA-256) composited into a corner.</summary>
public sealed record OverlayOperation(string File, string Target, string Image, string ImageSha256, IconCorner Corner) : IIconOperation
{
    public const IconCorner DefaultCorner = IconCorner.BottomRight;

    public string Kind
    {
        get
        {
            return OperationKinds.Overlay;
        }
    }

    public PixelRect? Apply(RgbaImage icon, IOperationSources sources)
    {
        return OverlayComposer.Draw(icon, sources.LoadOverlay(Image, ImageSha256), Corner);
    }

    public string Describe()
    {
        return $"overlay {File}:{Target} {Image} sha256 {ImageSha256.ToLowerInvariant()} {IconCorners.ToCode(Corner)}";
    }
}

/// <summary>Source-over alpha compositing of an overlay placed flush with a corner.</summary>
public static class OverlayComposer
{
    /// <summary>
    /// Composites <paramref name="overlay"/> onto <paramref name="icon"/> in place. Returns the bounding box of
    /// the overlay pixels that are not fully transparent (only those blocks need re-encoding), or null when
    /// the overlay is fully transparent.
    /// </summary>
    public static PixelRect? Draw(RgbaImage icon, RgbaImage overlay, IconCorner corner)
    {
        if (overlay.Width > icon.Width || overlay.Height > icon.Height)
        {
            throw new ArgumentException($"Overlay {overlay.Width}x{overlay.Height} does not fit into the {icon.Width}x{icon.Height} icon.");
        }

        PixelRect placement = IconCorners.GetRect(icon.Width, icon.Height, overlay.Width, overlay.Height, corner);
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;
        for (int y = 0; y < overlay.Height; y++)
        {
            for (int x = 0; x < overlay.Width; x++)
            {
                int source = overlay.GetOffset(x, y);
                if (overlay.Pixels[source + 3] == 0)
                {
                    continue;
                }

                int targetX = placement.X + x;
                int targetY = placement.Y + y;
                BlendPixel(overlay.Pixels, source, icon.Pixels, icon.GetOffset(targetX, targetY));
                left = Math.Min(left, targetX);
                top = Math.Min(top, targetY);
                right = Math.Max(right, targetX);
                bottom = Math.Max(bottom, targetY);
            }
        }

        if (left == int.MaxValue)
        {
            return null;
        }

        return new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private static void BlendPixel(byte[] source, int sourceOffset, byte[] target, int targetOffset)
    {
        double sourceAlpha = source[sourceOffset + 3] / 255.0;
        double targetAlpha = target[targetOffset + 3] / 255.0;
        double outAlpha = sourceAlpha + (targetAlpha * (1 - sourceAlpha));
        for (int channel = 0; channel < 3; channel++)
        {
            double value = (source[sourceOffset + channel] * sourceAlpha) + (target[targetOffset + channel] * targetAlpha * (1 - sourceAlpha));
            target[targetOffset + channel] = (byte)Math.Clamp(Math.Round(value / outAlpha), 0, 255);
        }

        target[targetOffset + 3] = (byte)Math.Clamp(Math.Round(outAlpha * 255), 0, 255);
    }
}
