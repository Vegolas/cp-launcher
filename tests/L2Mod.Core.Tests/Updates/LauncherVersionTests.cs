using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests.Updates;

public class LauncherVersionTests
{
    [Theory]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("10.20.30", "10.20.30")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.1")]
    [InlineData("1.0.0-x-y.0.z", "1.0.0-x-y.0.z")]
    [InlineData("1.0.0+5114f85", "1.0.0")]
    [InlineData("1.0.0-rc.1+build.7.sha-5114f85", "1.0.0-rc.1")]
    public void Parse_AcceptsSemVer(string text, string expected)
    {
        Assert.Equal(expected, LauncherVersion.Parse(text).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("-1.2.3")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3-a_b")]
    [InlineData("1.2.3+a..b")]
    [InlineData("2147483648.0.0")]
    [InlineData("1.2.3١")]
    public void TryParse_RejectsEverythingElse(string text)
    {
        Assert.False(LauncherVersion.TryParse(text, out _));
        Assert.Throws<FormatException>(() => LauncherVersion.Parse(text));
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        Assert.False(LauncherVersion.TryParse(null, out _));
    }

    [Fact]
    public void Ordering_FollowsSemVer()
    {
        string[] ascending =
        {
            "0.9.9",
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "1.0.1",
            "1.9.0",
            "1.10.0",
            "2.0.0",
        };

        for (int i = 1; i < ascending.Length; i++)
        {
            LauncherVersion lower = LauncherVersion.Parse(ascending[i - 1]);
            LauncherVersion higher = LauncherVersion.Parse(ascending[i]);
            Assert.True(lower < higher, $"{lower} < {higher}");
            Assert.True(higher > lower, $"{higher} > {lower}");
            Assert.Equal(-1, Math.Sign(lower.CompareTo(higher)));
        }
    }

    [Fact]
    public void BuildMetadata_DoesNotMatter()
    {
        LauncherVersion left = LauncherVersion.Parse("1.0.0+aaa");
        LauncherVersion right = LauncherVersion.Parse("1.0.0+bbb");

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(0, left.CompareTo(right));
    }

    [Fact]
    public void PreRelease_IsExposed()
    {
        LauncherVersion version = LauncherVersion.Parse("2.1.0-beta.3");

        Assert.Equal((2, 1, 0, "beta.3", true), (version.Major, version.Minor, version.Patch, version.PreRelease, version.IsPreRelease));
        Assert.False(LauncherVersion.Parse("2.1.0").IsPreRelease);
    }
}
