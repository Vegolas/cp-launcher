using L2Mod.Core.Packages;

namespace L2Mod.Core.Effects;

/// <param name="Messages">What was changed where (build output, technical details).</param>
public sealed record EffectBuildResult(byte[] FileBytes, IReadOnlyList<string> Messages);

/// <summary>
/// Applies effect operations to one system_season package, independent of their order in the manifest: in-place
/// changes first (effect swaps, the click arrow), then the appended icons over the target in the given order. A swap
/// renames classes, so an icon for a swapped skill goes on the class the game now plays for it.
/// </summary>
public static class EffectComposer
{
    /// <param name="fileName">The whitelisted client file, e.g. "LineageSkillEffect.u".</param>
    /// <param name="original">The original file as stored on disk.</param>
    /// <param name="iconSource">Opens the original Icon.utx the icons over the target are taken from; called only
    /// when there are icons to add. Returns null when there is no original Icon.utx to read.</param>
    public static EffectBuildResult Build(string fileName, byte[] original, IReadOnlyList<IEffectOperation> operations, Func<IconPackage?> iconSource)
    {
        foreach (IEffectOperation operation in operations)
        {
            if (!string.Equals(operation.File, fileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{operation.Describe()}' is for {operation.File}, not {fileName}.");
            }
        }

        L2File file = L2File.FromBytes(original);
        if (string.Equals(fileName, ClientFiles.LineageSkillEffect, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSkillEffects(file, operations, iconSource);
        }

        if (string.Equals(fileName, ClientFiles.LineageEffect, StringComparison.OrdinalIgnoreCase))
        {
            return BuildEffects(file, operations);
        }

        throw new ArgumentException($"{fileName} has no effect operations.");
    }

    private static EffectBuildResult BuildSkillEffects(L2File file, IReadOnlyList<IEffectOperation> operations, Func<IconPackage?> iconSource)
    {
        var swaps = new List<EffectSwapOperation>();
        var icons = new List<TargetIconOperation>();
        var swapped = new HashSet<int>();
        foreach (IEffectOperation operation in Distinct(operations))
        {
            if (operation is EffectSwapOperation swap)
            {
                if (swap.First == swap.Second || !swapped.Add(swap.First) || !swapped.Add(swap.Second))
                {
                    throw new ArgumentException($"'{swap.Describe()}': every skill can take part in one effect swap only.");
                }

                swaps.Add(swap);
            }
            else if (operation is TargetIconOperation icon)
            {
                icons.Add(icon);
            }
            else
            {
                throw new ArgumentException($"'{operation.Describe()}' cannot be applied to {ClientFiles.LineageSkillEffect}.");
            }
        }

        var messages = new List<string>();
        byte[] bytes = file.RawBytes;
        if (swaps.Count > 0)
        {
            bytes = file.BuildPatchedFile(SkillEffectSwaps.CreatePatches(file, swaps));
            messages.Add($"{swaps.Count} effect swap(s): {string.Join(", ", swaps.Select(swap => $"{swap.First} <-> {swap.Second}"))}");
        }

        if (icons.Count > 0)
        {
            IconPackage source = iconSource()
                ?? throw new InvalidDataException("There is no original Icon.utx to take the icons over the target from.");
            var builder = new TargetIconBuilder(file, bytes, source);
            foreach (TargetIconOperation icon in icons)
            {
                builder.AddIcon(icon);
            }

            bytes = builder.Build();
            messages.Add($"{icons.Count} icon(s) over the target: {string.Join(", ", icons.Select(icon => icon.Skill))}");
            messages.AddRange(builder.Messages);
        }

        return new EffectBuildResult(bytes, messages);
    }

    private static EffectBuildResult BuildEffects(L2File file, IReadOnlyList<IEffectOperation> operations)
    {
        bool arrow = false;
        foreach (IEffectOperation operation in operations)
        {
            if (operation is not GoldClickArrowOperation)
            {
                throw new ArgumentException($"'{operation.Describe()}' cannot be applied to {ClientFiles.LineageEffect}.");
            }

            arrow = true;
        }

        if (!arrow)
        {
            return new EffectBuildResult(file.RawBytes, Array.Empty<string>());
        }

        byte[] bytes = file.BuildPatchedFile(ClickArrowRecolor.CreatePatches(file));
        return new EffectBuildResult(bytes, new[] { "click-to-move arrow recoloured gold" });
    }

    /// <summary>The same operation listed by two packs is applied once.</summary>
    private static IEnumerable<IEffectOperation> Distinct(IReadOnlyList<IEffectOperation> operations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IEffectOperation operation in operations)
        {
            if (seen.Add(operation.Describe()))
            {
                yield return operation;
            }
        }
    }
}
