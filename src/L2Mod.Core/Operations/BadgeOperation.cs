using L2Mod.Core.Badges;
using L2Mod.Core.Imaging;

namespace L2Mod.Core.Operations;

/// <summary>A shrunken copy of another icon (<see cref="Badge"/>, taken from the unpatched package) in a corner.</summary>
public sealed record BadgeOperation(string File, string Target, string Badge, BadgeStyle Style) : IIconOperation
{
    public string Kind
    {
        get
        {
            return OperationKinds.Badge;
        }
    }

    public IconCorner Corner
    {
        get
        {
            return Style.Corner;
        }
    }

    public PixelRect? Apply(RgbaImage icon, IOperationSources sources)
    {
        return BadgeComposer.Draw(icon, sources.DecodeOriginal(Badge), Style);
    }

    public string Describe()
    {
        return $"badge {File}:{Target} <- {Badge} {Style.Size}px {IconCorners.ToCode(Style.Corner)} border {Style.Border} {Style.BorderColor} crop {Style.Crop}";
    }
}
