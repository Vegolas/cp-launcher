namespace L2Mod.Core.Imaging;

/// <summary>8-bit RGBA image, rows top to bottom.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Image dimensions must be positive.");
        }

        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("Pixel buffer size does not match the dimensions.");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int GetOffset(int x, int y)
    {
        return ((y * Width) + x) * 4;
    }

    public void SetPixel(int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        int offset = GetOffset(x, y);
        Pixels[offset] = red;
        Pixels[offset + 1] = green;
        Pixels[offset + 2] = blue;
        Pixels[offset + 3] = alpha;
    }

    public RgbaImage Clone()
    {
        return new RgbaImage(Width, Height, (byte[])Pixels.Clone());
    }

    public RgbaImage Crop(PixelRect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Right > Width || rect.Bottom > Height || rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentException($"Crop {rect} is outside the {Width}x{Height} image.");
        }

        var result = new RgbaImage(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
        {
            Array.Copy(Pixels, GetOffset(rect.X, rect.Y + y), result.Pixels, result.GetOffset(0, y), rect.Width * 4);
        }

        return result;
    }

    public void FillRectangle(PixelRect rect, byte red, byte green, byte blue)
    {
        for (int y = Math.Max(0, rect.Y); y < Math.Min(Height, rect.Bottom); y++)
        {
            for (int x = Math.Max(0, rect.X); x < Math.Min(Width, rect.Right); x++)
            {
                SetPixel(x, y, red, green, blue, 255);
            }
        }
    }

    /// <summary>Copies <paramref name="source"/> onto this image at (x, y), clipped to the bounds.</summary>
    public void DrawImage(RgbaImage source, int x, int y)
    {
        for (int sy = 0; sy < source.Height; sy++)
        {
            int ty = y + sy;
            if (ty < 0 || ty >= Height)
            {
                continue;
            }

            for (int sx = 0; sx < source.Width; sx++)
            {
                int tx = x + sx;
                if (tx < 0 || tx >= Width)
                {
                    continue;
                }

                Array.Copy(source.Pixels, source.GetOffset(sx, sy), Pixels, GetOffset(tx, ty), 4);
            }
        }
    }

    /// <summary>
    /// Area-averaging resize for arbitrary sizes: every target pixel averages the source pixels it covers,
    /// weighted by the covered fraction. Suited to shrinking icons into badges.
    /// </summary>
    public RgbaImage ResizeArea(int width, int height)
    {
        var result = new RgbaImage(width, height);
        double scaleX = (double)Width / width;
        double scaleY = (double)Height / height;
        double[] sums = new double[4];
        for (int ty = 0; ty < height; ty++)
        {
            double top = ty * scaleY;
            double bottom = (ty + 1) * scaleY;
            for (int tx = 0; tx < width; tx++)
            {
                double left = tx * scaleX;
                double right = (tx + 1) * scaleX;
                Array.Clear(sums);
                double totalWeight = 0;
                for (int sy = (int)Math.Floor(top); sy < Math.Min(Height, (int)Math.Ceiling(bottom)); sy++)
                {
                    double weightY = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
                    for (int sx = (int)Math.Floor(left); sx < Math.Min(Width, (int)Math.Ceiling(right)); sx++)
                    {
                        double weight = weightY * (Math.Min(right, sx + 1) - Math.Max(left, sx));
                        int source = GetOffset(sx, sy);
                        for (int channel = 0; channel < 4; channel++)
                        {
                            sums[channel] += Pixels[source + channel] * weight;
                        }

                        totalWeight += weight;
                    }
                }

                int target = result.GetOffset(tx, ty);
                for (int channel = 0; channel < 4; channel++)
                {
                    result.Pixels[target + channel] = (byte)Math.Clamp(Math.Round(sums[channel] / totalWeight), 0, 255);
                }
            }
        }

        return result;
    }

    public bool HasTransparency()
    {
        for (int offset = 3; offset < Pixels.Length; offset += 4)
        {
            if (Pixels[offset] != 255)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Box filter downscale by an integer factor (used for larger source art and mip chains).</summary>
    public RgbaImage DownscaleBox(int factor)
    {
        if (factor < 1 || Width % factor != 0 || Height % factor != 0)
        {
            throw new ArgumentException($"Cannot downscale {Width}x{Height} by {factor}.");
        }

        if (factor == 1)
        {
            return this;
        }

        var result = new RgbaImage(Width / factor, Height / factor);
        int samples = factor * factor;
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                int[] sums = new int[4];
                for (int dy = 0; dy < factor; dy++)
                {
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int source = GetOffset((x * factor) + dx, (y * factor) + dy);
                        for (int channel = 0; channel < 4; channel++)
                        {
                            sums[channel] += Pixels[source + channel];
                        }
                    }
                }

                int target = result.GetOffset(x, y);
                for (int channel = 0; channel < 4; channel++)
                {
                    result.Pixels[target + channel] = (byte)((sums[channel] + (samples / 2)) / samples);
                }
            }
        }

        return result;
    }

    /// <summary>Nearest-neighbour upscale, only for previews.</summary>
    public RgbaImage UpscaleNearest(int factor)
    {
        if (factor < 1)
        {
            throw new ArgumentException("Scale factor must be at least 1.");
        }

        if (factor == 1)
        {
            return this;
        }

        var result = new RgbaImage(Width * factor, Height * factor);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                Array.Copy(Pixels, GetOffset(x / factor, y / factor), result.Pixels, result.GetOffset(x, y), 4);
            }
        }

        return result;
    }
}
