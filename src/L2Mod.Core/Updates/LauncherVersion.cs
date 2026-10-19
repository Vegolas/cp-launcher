using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace L2Mod.Core.Updates;

/// <summary>
/// A launcher version in the SemVer 2.0 form <c>MAJOR.MINOR.PATCH[-pre.release][+build]</c>, parsed strictly:
/// no 'v' prefix, no fourth part, no whitespace, no leading zeros in numbers. Build metadata is accepted
/// (the SDK appends <c>+&lt;commit&gt;</c> to the informational version) but dropped: it plays no part in
/// equality or ordering. A pre-release sorts before its release (1.1.0-beta.1 &lt; 1.1.0).
/// </summary>
public sealed class LauncherVersion : IComparable<LauncherVersion>, IEquatable<LauncherVersion>
{
    private const int MaxLength = 128;

    private readonly string[] _preRelease;

    private LauncherVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The pre-release part without the '-', or an empty string for a release.</summary>
    public string PreRelease
    {
        get
        {
            return string.Join('.', _preRelease);
        }
    }

    public bool IsPreRelease
    {
        get
        {
            return _preRelease.Length > 0;
        }
    }

    public static LauncherVersion Parse(string text)
    {
        if (!TryParse(text, out LauncherVersion? version))
        {
            throw new FormatException($"'{text}' is not a version like 1.2.3 (optionally -pre.release and +build).");
        }

        return version;
    }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out LauncherVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        string rest = text;
        int plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            if (!AreValidIdentifiers(rest.Substring(plus + 1), numericRules: false))
            {
                return false;
            }

            rest = rest.Substring(0, plus);
        }

        string[] preRelease = Array.Empty<string>();
        int dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            string preReleaseText = rest.Substring(dash + 1);
            if (!AreValidIdentifiers(preReleaseText, numericRules: true))
            {
                return false;
            }

            preRelease = preReleaseText.Split('.');
            rest = rest.Substring(0, dash);
        }

        string[] parts = rest.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseNumber(parts[0], out int major) || !TryParseNumber(parts[1], out int minor) || !TryParseNumber(parts[2], out int patch))
        {
            return false;
        }

        version = new LauncherVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(LauncherVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        if (result == 0)
        {
            result = ComparePreRelease(_preRelease, other._preRelease);
        }

        return result;
    }

    public bool Equals(LauncherVersion? other)
    {
        return other is not null && CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as LauncherVersion);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor, Patch, PreRelease);
    }

    /// <summary>MAJOR.MINOR.PATCH plus the pre-release part; never the build metadata.</summary>
    public override string ToString()
    {
        string core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (_preRelease.Length == 0)
        {
            return core;
        }

        return core + "-" + PreRelease;
    }

    public static bool operator ==(LauncherVersion? left, LauncherVersion? right)
    {
        if (left is null)
        {
            return right is null;
        }

        return left.Equals(right);
    }

    public static bool operator !=(LauncherVersion? left, LauncherVersion? right)
    {
        return !(left == right);
    }

    public static bool operator <(LauncherVersion left, LauncherVersion right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(LauncherVersion left, LauncherVersion right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(LauncherVersion left, LauncherVersion right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(LauncherVersion left, LauncherVersion right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>SemVer §11: no pre-release beats any pre-release; identifiers compare left to right,
    /// numeric ones numerically and below alphanumeric ones; a shorter prefix sorts first.</summary>
    private static int ComparePreRelease(string[] left, string[] right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return right.Length.CompareTo(left.Length);
        }

        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            bool leftNumeric = IsDigits(left[i]);
            bool rightNumeric = IsDigits(right[i]);
            int result;
            if (leftNumeric && rightNumeric)
            {
                result = left[i].Length != right[i].Length
                    ? left[i].Length.CompareTo(right[i].Length)
                    : string.CompareOrdinal(left[i], right[i]);
            }
            else if (leftNumeric)
            {
                result = -1;
            }
            else if (rightNumeric)
            {
                result = 1;
            }
            else
            {
                result = string.CompareOrdinal(left[i], right[i]);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || !IsDigits(text) || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Dot-separated, non-empty identifiers of [0-9A-Za-z-]; with <paramref name="numericRules"/>
    /// purely numeric identifiers must not have leading zeros (pre-release rule).</summary>
    private static bool AreValidIdentifiers(string text, bool numericRules)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (string identifier in text.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (char c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }

            if (numericRules && identifier.Length > 1 && identifier[0] == '0' && IsDigits(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigits(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
