using System.Globalization;

namespace L2Mod.Core.Imaging;

public readonly record struct Rgb(byte Red, byte Green, byte Blue)
{
    public static Rgb Black
    {
        get
        {
            return new Rgb(0, 0, 0);
        }
    }

    /// <summary>Parses "RRGGBB" or "#RRGGBB".</summary>
    public static Rgb ParseHex(string value)
    {
        string hex = value.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
        {
            throw new ArgumentException($"Colour '{value}' must be in RRGGBB hex form.");
        }

        return new Rgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    public override string ToString()
    {
        return $"#{Red:X2}{Green:X2}{Blue:X2}";
    }
}
