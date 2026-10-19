using L2Mod.Cli;
using L2Mod.Core.Imaging;

namespace L2Mod.Core.Tests;

public class ImagingAndCommandLineTests
{
    [Fact]
    public void ResizeArea_KeepsSolidColours()
    {
        RgbaImage image = TestPackageBuilder.SolidImage(32, 32, 12, 34, 56);

        RgbaImage resized = image.ResizeArea(14, 14);

        Assert.Equal(TestPackageBuilder.SolidImage(14, 14, 12, 34, 56).Pixels, resized.Pixels);
    }

    [Fact]
    public void ResizeArea_MatchesBoxFilterForIntegerFactors()
    {
        RgbaImage image = TestPackageBuilder.NoiseImage(32, 32, 3);

        RgbaImage area = image.ResizeArea(16, 16);
        RgbaImage box = image.DownscaleBox(2);

        for (int i = 0; i < area.Pixels.Length; i++)
        {
            Assert.InRange(Math.Abs(area.Pixels[i] - box.Pixels[i]), 0, 1);
        }
    }

    [Fact]
    public void ResizeArea_WeightsPartialCoverage()
    {
        var image = new RgbaImage(3, 1);
        image.SetPixel(0, 0, 0, 0, 0, 255);
        image.SetPixel(1, 0, 90, 90, 90, 255);
        image.SetPixel(2, 0, 180, 180, 180, 255);

        RgbaImage resized = image.ResizeArea(2, 1);

        Assert.Equal(30, resized.Pixels[0]);
        Assert.Equal(150, resized.Pixels[4]);
    }

    [Fact]
    public void PreviewSheet_LaysOutPairsInGrid()
    {
        var pair = new PreviewPair(TestPackageBuilder.SolidImage(32, 32, 255, 0, 0), TestPackageBuilder.SolidImage(32, 32, 0, 255, 0));

        RgbaImage sheet = PreviewSheet.Build(new[] { pair, pair, pair }, 2, 2);

        int cellWidth = (64 * 2) + 2 + 12;
        int cellHeight = 64 + 12;
        Assert.Equal(cellWidth * 2, sheet.Width);
        Assert.Equal(cellHeight * 2, sheet.Height);
        Assert.Equal(255, sheet.Pixels[sheet.GetOffset(6, 6)]);
        Assert.Equal(255, sheet.Pixels[sheet.GetOffset(6 + 64 + 2, 6) + 1]);
    }

    [Fact]
    public void CommandLine_SeparatesPositionalOptionsAndFlags()
    {
        string[] arguments = { "in.utx", "--size", "12", "map.txt", "--overwrite", "out.utx" };

        CommandLine command = CommandLine.Parse(arguments, new[] { "size" }, new[] { "overwrite" });

        Assert.Equal(new[] { "in.utx", "map.txt", "out.utx" }, command.Positional);
        Assert.Equal(12, command.GetInt("size", 16, 4, 32));
        Assert.True(command.HasFlag("overwrite"));
        Assert.Null(command.GetOption("corner"));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--size")]
    public void CommandLine_RejectsBadOptions(string argument)
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(new[] { argument }, new[] { "size" }, Array.Empty<string>()));
    }

    [Fact]
    public void CommandLine_ValidatesIntegerRange()
    {
        CommandLine command = CommandLine.Parse(new[] { "--size", "99" }, new[] { "size" }, Array.Empty<string>());

        Assert.Throws<ArgumentException>(() => command.GetInt("size", 16, 4, 32));
    }
}
