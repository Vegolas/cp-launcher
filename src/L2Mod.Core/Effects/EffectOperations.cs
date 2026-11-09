using System.Globalization;
using L2Mod.Core.Operations;

namespace L2Mod.Core.Effects;

/// <summary>A change of a skill-effect package (system_season\*.u) rather than of an icon texture.</summary>
public interface IEffectOperation : IPatchOperation
{
}

public static class EffectKinds
{
    public const string EffectSwap = "effect-swap";
    public const string GoldClickArrow = "gold-click-arrow";
    public const string TargetIcon = "target-icon";

    /// <summary>Skill ids are written into class names (l2_&lt;id&gt;_skill), at most five digits.</summary>
    public const int MaxSkillId = 99999;

    public const int MinIconSeconds = 1;
    public const int MaxIconSeconds = 60;
    public const int DefaultIconSeconds = 5;

    public static IReadOnlyList<string> All
    {
        get
        {
            return new[] { EffectSwap, GoldClickArrow, TargetIcon };
        }
    }

    public static bool IsValidSkillId(int id)
    {
        return id > 0 && id <= MaxSkillId;
    }

    /// <summary>The effect class the game plays for a skill: l2_&lt;id&gt;_skill.</summary>
    public static string SkillClassName(int skillId)
    {
        return $"l2_{skillId.ToString(CultureInfo.InvariantCulture)}_skill";
    }

    /// <summary>The icon texture of a skill in Icon.utx: skill_i.skill&lt;id, at least 4 digits&gt;.</summary>
    public static string SkillIconPath(int skillId)
    {
        return $"skill_i.skill{skillId.ToString("D4", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// Two skills swap their effects both ways (LineageSkillEffect.u): the class names l2_&lt;a&gt;_skill and
/// l2_&lt;b&gt;_skill swap places in the name table and so do the classes' SkillID defaults. Size-preserving.
/// </summary>
public sealed record EffectSwapOperation(int First, int Second) : IEffectOperation
{
    public string Kind
    {
        get
        {
            return EffectKinds.EffectSwap;
        }
    }

    public string File
    {
        get
        {
            return ClientFiles.LineageSkillEffect;
        }
    }

    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Kind} {File} {First} {Second}");
    }
}

/// <summary>
/// The click-to-move arrow turns gold (LineageEffect.u): blue tints of its four effect classes become gold, the green
/// mesh material a neutral grey one and the blue glow sprite a golden one. Size-preserving.
/// </summary>
public sealed record GoldClickArrowOperation : IEffectOperation
{
    public string Kind
    {
        get
        {
            return EffectKinds.GoldClickArrow;
        }
    }

    public string File
    {
        get
        {
            return ClientFiles.LineageEffect;
        }
    }

    public string Describe()
    {
        return $"{Kind} {File}";
    }
}

/// <summary>
/// The skill's icon floats up over the target's head for <see cref="Seconds"/> whenever the game plays the effect
/// class l2_&lt;<see cref="EffectClass"/>&gt;_skill on a target (LineageSkillEffect.u, appended). It shows on every
/// cast that reaches the target, also a resisted one, and every other skill playing that class shows it too.
/// </summary>
public sealed record TargetIconOperation(int Skill, int EffectClass, int Seconds) : IEffectOperation
{
    public string Kind
    {
        get
        {
            return EffectKinds.TargetIcon;
        }
    }

    public string File
    {
        get
        {
            return ClientFiles.LineageSkillEffect;
        }
    }

    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Kind} {File} {Skill} @{EffectClass} {Seconds}s");
    }
}
