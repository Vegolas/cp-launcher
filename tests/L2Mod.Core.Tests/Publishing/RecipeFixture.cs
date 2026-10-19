using L2Mod.Core.Imaging;

namespace L2Mod.Core.Tests.Publishing;

/// <summary>
/// A synthetic recipes folder plus the synthetic Icon.utx (<see cref="TestIcons"/>) in a temp folder:
/// a badge pack on skill0365, a tag pack on skill0102 and an overlay pack on skill1059 (corner tr),
/// one announcement and one Option.ini preset.
/// </summary>
public sealed class RecipeFixture : IDisposable
{
    public const string Version = "2026.10.20.1";

    public static readonly string[] PackIds = { "badges", "tags", "marks" };

    public RecipeFixture()
    {
        OriginalBytes = TestIcons.BuildIconUtx();
        OriginalPath = Temp.WriteBytes(Path.Combine("client", "Icon.utx"), OriginalBytes);
        RecipesPath = Temp.Combine("recipes");
        Temp.WriteText(Path.Combine("recipes", "mappings", "badges.txt"), "# badges\nskill0365  skill1303  # Dance -> Might\n");
        Temp.WriteText(Path.Combine("recipes", "mappings", "tags.txt"), "skill0102  CC  # Stun\n");
        Temp.WriteText(Path.Combine("recipes", "mappings", "marks.txt"), "skill1059  marks/dot.png  # mark\n");
        Temp.WriteBytes(Path.Combine("recipes", "mappings", "marks", "dot.png"), PngCodec.Encode(OverlayImage()));
        WriteRecipes(DefaultRecipesJson);
    }

    public TempDirectory Temp { get; } = new TempDirectory();

    public byte[] OriginalBytes { get; }

    public string OriginalPath { get; }

    public string RecipesPath { get; }

    public string DistPath
    {
        get
        {
            return Temp.Combine("dist");
        }
    }

    public string PreviewPath
    {
        get
        {
            return Temp.Combine("previews");
        }
    }

    public static string DefaultRecipesJson
    {
        get
        {
            return """
                {
                  // synthetic recipes for the tests
                  "format": 1,
                  "version": "VERSION",
                  "packs": [
                    { "id": "badges", "name": "Znaczki tańców", "description": ["Linia 1", "Linia 2"], "default": true, "type": "badge", "mapping": "mappings/badges.txt" },
                    { "id": "tags", "name": "Tagi debuffów", "default": true, "type": "tag", "mapping": "mappings/tags.txt" },
                    { "id": "marks", "name": "Oznaczenia", "type": "overlay", "mapping": "mappings/marks.txt", "corner": "tr" },
                  ],
                  "announcements": [ { "date": "2026-10-20", "title": "Start", "text": ["Pierwsza linia.", "Druga linia."] } ],
                  "optionPresets": [ { "id": "mass-pvp", "name": "Masowe PvP", "values": { "Video": { "EffectDetail": 0 } } } ],
                }
                """.Replace("VERSION", Version);
        }
    }

    /// <summary>8x8, opaque white 6x6 square with a transparent 1 px frame.</summary>
    public static RgbaImage OverlayImage()
    {
        var image = new RgbaImage(8, 8);
        image.FillRectangle(new PixelRect(1, 1, 6, 6), 255, 255, 255);
        return image;
    }

    public void WriteRecipes(string json)
    {
        Temp.WriteText(Path.Combine("recipes", "recipes.json"), json);
    }

    public void Dispose()
    {
        Temp.Dispose();
    }
}
