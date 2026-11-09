using L2Mod.Core.Effects;
using L2Mod.Core.Imaging;
using L2Mod.Core.Packages;
using L2Mod.Core.Textures;

namespace L2Mod.Core.Tests;

public class EffectEngineTests
{
    private static readonly IconPackage Icons = TestIcons.Open(TestEffectPackages.BuildIconUtx());

    [Fact]
    public void Swap_ExchangesClassNamesAndSkillIdsBothWays_SizePreserving()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();

        EffectBuildResult result = Build(original, new EffectSwapOperation(1235, 1175));

        Assert.Equal(original.Length, result.FileBytes.Length);
        Assert.Equal(
            new[]
            {
                "l2_1235_skill (SkillID 1235) -> fx_1175",
                "l2_1175_skill (SkillID 1175) -> fx_1235",
                "l2_1071_skill (SkillID 1071) -> fx_1071",
            },
            SkillEffectSwaps.DescribeSkillClasses(result.FileBytes, new[] { 1235, 1175, 1071 }));
    }

    [Fact]
    public void Swap_RefusesASkillWithoutAClass()
    {
        Assert.Throws<InvalidDataException>(() => Build(TestEffectPackages.BuildSkillEffects(), new EffectSwapOperation(1235, 9999)));
    }

    [Fact]
    public void TargetIcon_AppendsAFlippedIconEffectToTheSkillsShotAction()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();

        EffectBuildResult result = Build(original, new TargetIconOperation(1071, 1071, 5));

        Assert.True(result.FileBytes.Length > original.Length);
        Assert.Equal(original.AsSpan(64, 32).ToArray(), result.FileBytes.AsSpan(64, 32).ToArray());
        Assert.Equal(
            new[] { "l2_1071_skill (SkillID 1071) -> fx_1071, cp_icon_1071", "l2_1048_skill (SkillID 1048) -> fx_1048" },
            SkillEffectSwaps.DescribeSkillClasses(result.FileBytes, new[] { 1071, 1048 }));
        UnrealPackage built = UnrealPackage.Parse(result.FileBytes);
        RgbaImage shown = Flip(Decode(built, "cp_icon_tex_1071"));
        Assert.Equal(Icons.Decode(Icons.FindTexture("skill_i.skill1071")).Pixels, shown.Pixels);
        Assert.Contains(result.Messages, message => message.Contains("1 icons match their source flipped", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetIcon_GoesOnTheClassTheSkillPlays()
    {
        EffectBuildResult result = Build(TestEffectPackages.BuildSkillEffects(), new TargetIconOperation(1323, 1048, 7));

        Assert.Equal(
            new[] { "l2_1048_skill (SkillID 1048) -> fx_1048, cp_icon_1323" },
            SkillEffectSwaps.DescribeSkillClasses(result.FileBytes, new[] { 1048 }));
        Assert.Contains("1323: icon added to l2_1048_skill.ShotAction", result.Messages);
    }

    [Fact]
    public void TargetIcon_RefusesASkillWithoutAClassOfItsOwn()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Build(TestEffectPackages.BuildSkillEffects(), new TargetIconOperation(1323, 1323, 5)));

        Assert.Contains("@<id>", exception.Message);
    }

    [Fact]
    public void TargetIcon_NeedsTheOriginalIconUtx()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();

        Assert.Throws<InvalidDataException>(() => EffectComposer.Build(ClientFiles.LineageSkillEffect, original, new IEffectOperation[] { new TargetIconOperation(1071, 1071, 5) }, () =>
        {
            return null;
        }));
    }

    [Fact]
    public void TargetIcon_RefusesAnEncryptedPackage()
    {
        byte[] encrypted = TestPackageBuilder.Encrypt(TestEffectPackages.BuildSkillEffects(), "111", 0xAC);

        Assert.Throws<NotSupportedException>(() => Build(encrypted, new TargetIconOperation(1071, 1071, 5)));
    }

    [Fact]
    public void Composer_AppliesSwapsBeforeIconsWhateverTheOrder()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();
        var swap = new EffectSwapOperation(1235, 1175);
        var icon = new TargetIconOperation(1071, 1071, 5);

        byte[] swapFirst = Build(original, swap, icon).FileBytes;
        byte[] iconFirst = Build(original, icon, swap).FileBytes;

        Assert.Equal(swapFirst, iconFirst);
    }

    [Fact]
    public void Composer_AppliesTheSameOperationOnce()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();
        var icon = new TargetIconOperation(1071, 1071, 5);

        Assert.Equal(Build(original, icon).FileBytes, Build(original, icon, icon).FileBytes);
    }

    [Theory]
    [InlineData(1235, 1175, 1235, 1230)]
    [InlineData(1235, 1175, 1230, 1175)]
    public void Composer_RefusesASkillInTwoSwaps(int a, int b, int c, int d)
    {
        Assert.Throws<ArgumentException>(() => Build(TestEffectPackages.BuildSkillEffects(), new EffectSwapOperation(a, b), new EffectSwapOperation(c, d)));
    }

    [Fact]
    public void Composer_RefusesTwoIconsForOneClassAndOperationsForAnotherFile()
    {
        byte[] original = TestEffectPackages.BuildSkillEffects();

        Assert.Throws<ArgumentException>(() => Build(original, new TargetIconOperation(1071, 1048, 5), new TargetIconOperation(1323, 1048, 5)));
        Assert.Throws<ArgumentException>(() => Build(original, new GoldClickArrowOperation()));
    }

    [Fact]
    public void ClickArrow_SwapsTheBlueGlowAndGreenMaterialOfTheArrowClassesOnly()
    {
        byte[] original = TestEffectPackages.BuildEffects();

        EffectBuildResult result = EffectComposer.Build(ClientFiles.LineageEffect, original, new IEffectOperation[] { new GoldClickArrowOperation() }, () =>
        {
            return null;
        });

        Assert.Equal(original.Length, result.FileBytes.Length);
        L2File file = L2File.FromBytes(result.FileBytes);
        Assert.Equal(L2Encryption.Ver111, file.Encryption);
        UnrealPackage package = UnrealPackage.Parse(file.Body);
        Assert.Equal("fx_m_t_3037", ReferencedObject(package, "GstarFX_arrow_click.SpriteEmitter0", "Texture"));
        Assert.Equal("aura001_1", ReferencedObject(package, "GstarFX_arrow_click.MeshEmitter0", "CustomMaterials"));
        Assert.Equal("FX_M_T4111", ReferencedObject(package, "GstarFX_other.SpriteEmitter0", "Texture"));
    }

    [Fact]
    public void ClickArrow_RefusesAPackageWithoutTheArrowTextures()
    {
        Assert.Throws<InvalidDataException>(() => EffectComposer.Build(ClientFiles.LineageEffect, TestEffectPackages.BuildSkillEffects(), new IEffectOperation[] { new GoldClickArrowOperation() }, () =>
        {
            return null;
        }));
    }

    [Fact]
    public void FlipDxt1_IsItsOwnInverseAndFlipsThePicture()
    {
        RgbaImage image = TestPackageBuilder.NoiseImage(32, 32, 7);
        byte[] encoded = Dxt1Codec.Encode(image);

        byte[] flipped = TargetIconBuilder.FlipDxt1(encoded, 32, 32);

        Assert.Equal(encoded, TargetIconBuilder.FlipDxt1(flipped, 32, 32));
        Assert.Equal(Flip(Dxt1Codec.Decode(encoded, 32, 32)).Pixels, Dxt1Codec.Decode(flipped, 32, 32).Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(123)]
    [InlineData(int.MinValue + 1)]
    [InlineData(int.MaxValue)]
    public void CompactIndex_RoundTrips(int value)
    {
        byte[] encoded = CompactIndex.Encode(value);
        int offset = 0;

        Assert.Equal(value, CompactIndex.Read(encoded, ref offset));
        Assert.Equal(encoded.Length, offset);
    }

    private static EffectBuildResult Build(byte[] original, params IEffectOperation[] operations)
    {
        return EffectComposer.Build(ClientFiles.LineageSkillEffect, original, operations, () =>
        {
            return Icons;
        });
    }

    private static RgbaImage Decode(UnrealPackage package, string path)
    {
        TextureData texture = TextureReader.Read(package, package.Exports.Single(export => package.GetPath(export) == path));
        MipLevel mip = texture.Mips[0];
        return Dxt1Codec.Decode(package.Data.AsSpan(mip.DataOffset, mip.DataSize), mip.Width, mip.Height);
    }

    private static RgbaImage Flip(RgbaImage image)
    {
        byte[] pixels = new byte[image.Pixels.Length];
        int row = image.Width * 4;
        for (int y = 0; y < image.Height; y++)
        {
            image.Pixels.AsSpan((image.Height - 1 - y) * row, row).CopyTo(pixels.AsSpan(y * row, row));
        }

        return new RgbaImage(image.Width, image.Height, pixels);
    }

    /// <summary>The object the first reference of a property points at (an object property or an array's element).</summary>
    private static string ReferencedObject(UnrealPackage package, string path, string property)
    {
        ExportEntry export = package.Exports.Single(e => package.GetPath(e) == path);
        PropertyTag tag = PropertyTags.Read(package.Data, export.SerialOffset, export.SerialOffset + export.SerialSize, package.GetName).Single(t => t.Name == property);
        int offset = tag.ValueOffset;
        if (tag.Type == PropertyTypes.Array)
        {
            CompactIndex.Read(package.Data, ref offset);
        }

        return package.GetObjectName(CompactIndex.Read(package.Data, ref offset));
    }
}
