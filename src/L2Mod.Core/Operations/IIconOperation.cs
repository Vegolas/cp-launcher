using L2Mod.Core.Imaging;

namespace L2Mod.Core.Operations;

/// <summary>One change of one whitelisted client file, as listed in a manifest pack.</summary>
public interface IPatchOperation
{
    /// <summary>The manifest "op" value (see <see cref="OperationKinds"/> and <c>EffectKinds</c>).</summary>
    string Kind { get; }

    /// <summary>Client file name, e.g. "Icon.utx".</summary>
    string File { get; }

    /// <summary>Stable one-line description (logs, warnings and the recipe key of an installed patch).</summary>
    string Describe();
}

/// <summary>
/// One size-preserving change of one texture in one client file. Operations on the same texture are drawn
/// in order onto one RGBA image, which is then encoded once.
/// </summary>
public interface IIconOperation : IPatchOperation
{
    /// <summary>Texture name as accepted by <see cref="IconPackage.FindTexture"/>.</summary>
    string Target { get; }

    IconCorner Corner { get; }

    /// <summary>
    /// Draws onto <paramref name="icon"/> in place. Returns the rectangle whose DXT1 blocks have to be
    /// re-encoded, or null when nothing changed.
    /// </summary>
    PixelRect? Apply(RgbaImage icon, IOperationSources sources);
}

/// <summary>Inputs an operation may need besides the icon it draws on.</summary>
public interface IOperationSources
{
    /// <summary>A texture of the package as it was before any operation. Callers must not modify it.</summary>
    RgbaImage DecodeOriginal(string textureName);

    /// <summary>An overlay image, checked against its hash and the size limits. Callers must not modify it.</summary>
    RgbaImage LoadOverlay(string image, string sha256);
}

public static class OperationKinds
{
    public const string Badge = "badge";
    public const string Tag = "tag";
    public const string Overlay = "overlay";

    public static IReadOnlyList<string> All
    {
        get
        {
            return new[] { Badge, Tag, Overlay };
        }
    }
}
