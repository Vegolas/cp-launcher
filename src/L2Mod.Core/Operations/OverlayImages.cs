using L2Mod.Core.Imaging;

namespace L2Mod.Core.Operations;

/// <summary>Read access to downloaded assets by their manifest path ("assets/&lt;sha256&gt;.png").</summary>
public interface IAssetStore
{
    byte[] ReadAsset(string path);
}

/// <summary>Validation of overlay PNGs, which come from the network: hash first, then size limits, then decoding.</summary>
public static class OverlayImages
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxSize = 32;

    public static RgbaImage Decode(byte[] png, string expectedSha256)
    {
        if (png.Length > MaxBytes)
        {
            throw new InvalidDataException($"Overlay image has {png.Length} bytes; the limit is {MaxBytes}.");
        }

        if (!ContentHash.Matches(expectedSha256, png))
        {
            throw new InvalidDataException($"Overlay image does not match its SHA-256 {expectedSha256}.");
        }

        (int width, int height) = PngCodec.ReadSize(png);
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
        {
            throw new InvalidDataException($"Overlay image is {width}x{height}; the limit is {MaxSize}x{MaxSize}.");
        }

        return PngCodec.Decode(png);
    }
}
