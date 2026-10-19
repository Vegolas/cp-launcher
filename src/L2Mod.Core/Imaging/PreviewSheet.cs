namespace L2Mod.Core.Imaging;

public sealed record PreviewPair(RgbaImage Before, RgbaImage After);

/// <summary>Grid of before/after pairs (no labels; order follows the input).</summary>
public static class PreviewSheet
{
    private const int Padding = 6;
    private const int PairGap = 2;
    private const byte Background = 40;

    public static RgbaImage Build(IReadOnlyList<PreviewPair> pairs, int scale, int columns)
    {
        if (pairs.Count == 0)
        {
            throw new ArgumentException("Nothing to preview.");
        }

        int iconWidth = pairs[0].Before.Width * scale;
        int iconHeight = pairs[0].Before.Height * scale;
        int cellWidth = (iconWidth * 2) + PairGap + (Padding * 2);
        int cellHeight = iconHeight + (Padding * 2);
        int columnCount = Math.Min(columns, pairs.Count);
        int rowCount = (pairs.Count + columnCount - 1) / columnCount;
        var sheet = new RgbaImage(cellWidth * columnCount, cellHeight * rowCount);
        sheet.FillRectangle(new PixelRect(0, 0, sheet.Width, sheet.Height), Background, Background, Background);
        for (int i = 0; i < pairs.Count; i++)
        {
            int x = ((i % columnCount) * cellWidth) + Padding;
            int y = ((i / columnCount) * cellHeight) + Padding;
            sheet.DrawImage(pairs[i].Before.UpscaleNearest(scale), x, y);
            sheet.DrawImage(pairs[i].After.UpscaleNearest(scale), x + iconWidth + PairGap, y);
        }

        return sheet;
    }
}
