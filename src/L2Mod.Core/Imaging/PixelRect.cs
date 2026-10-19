namespace L2Mod.Core.Imaging;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right
    {
        get
        {
            return X + Width;
        }
    }

    public int Bottom
    {
        get
        {
            return Y + Height;
        }
    }

    public bool IsEmpty
    {
        get
        {
            return Width <= 0 || Height <= 0;
        }
    }

    public bool Contains(int x, int y)
    {
        return x >= X && x < Right && y >= Y && y < Bottom;
    }

    /// <summary>The overlapping part of both rectangles; empty when they do not overlap.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        if (right <= left || bottom <= top)
        {
            return new PixelRect(left, top, 0, 0);
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }

    public bool Intersects(PixelRect other)
    {
        return !Intersect(other).IsEmpty;
    }
}
