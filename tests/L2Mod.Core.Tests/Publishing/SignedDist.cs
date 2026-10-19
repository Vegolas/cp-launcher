using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Core.Tests.Publishing;

/// <summary>Signed dist folders built from a <see cref="RecipeFixture"/>.</summary>
internal static class SignedDist
{
    /// <summary>Builds the fixture's recipes into a new <paramref name="directory"/> and signs the manifest.</summary>
    public static DistBuildResult Write(RecipeFixture fixture, string directory, SigningKeyPair keys, string? version = null)
    {
        DistBuildResult build = DistBuilder.Build(RecipeProject.Load(fixture.RecipesPath), fixture.OriginalPath, new DistBuildOptions { Version = version });
        DistWriter.Write(directory, build, false);
        File.WriteAllBytes(Path.Combine(directory, DistLayout.SignatureFile), ManifestSignature.Sign(build.ManifestBytes, keys.PrivateKeyPem));
        return build;
    }

    /// <summary>Adds a launcher release with these bytes to the fixture's recipes.</summary>
    public static void AddLauncher(RecipeFixture fixture, string version, string content)
    {
        fixture.Temp.WriteBytes(Path.Combine("bin", "CpLauncher.exe"), Encoding.ASCII.GetBytes(content));
        string launcher = $"\"launcher\": {{ \"version\": \"{version}\", \"file\": \"../bin/CpLauncher.exe\" }},\n\"packs\":";
        fixture.WriteRecipes(RecipeFixture.DefaultRecipesJson.Replace("\"packs\":", launcher));
    }

    /// <summary>Replaces the overlay image, so the next build references a different asset.</summary>
    public static void ChangeOverlay(RecipeFixture fixture)
    {
        RgbaImage image = RecipeFixture.OverlayImage();
        image.FillRectangle(new PixelRect(2, 2, 2, 2), 255, 0, 0);
        fixture.Temp.WriteBytes(Path.Combine("recipes", "mappings", "marks", "dot.png"), PngCodec.Encode(image));
    }
}
