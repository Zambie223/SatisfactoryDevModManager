using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "")]
    [InlineData("v1.2.3", 1, 2, 3, "", "")]
    [InlineData("1.0.0-dev.4", 1, 0, 0, "dev.4", "")]
    [InlineData("1.0.0-rc.1+build.7", 1, 0, 0, "rc.1", "build.7")]
    public void Parses(string text, long ma, long mi, long pa, string pre, string build)
    {
        var v = SemVersion.Parse(text);
        Assert.Equal((ma, mi, pa, pre, build), (v.Major, v.Minor, v.Patch, v.PreRelease, v.Build));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("^1.2.3")]
    [InlineData("")]
    public void RejectsInvalid(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void OrdersBySemVerPrecedence()
    {
        // The SemVer 2.0 spec's example chain.
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        for (int i = 1; i < ordered.Length; i++)
            Assert.True(SemVersion.Parse(ordered[i - 1]) < SemVersion.Parse(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
    }

    [Fact]
    public void EqualNumericPrereleaseFieldsCompareEqual()
    {
        // SML's ComparePreReleaseField returns -1 for equal numeric fields, so SML would order these wrongly.
        Assert.True(SemVersion.Parse("1.0.0-rc.1.6") > SemVersion.Parse("1.0.0-rc.1.5"));
        Assert.Equal(SemVersion.Parse("1.0.0-dev.3"), SemVersion.Parse("1.0.0-dev.3+other"));
    }
}

public class VersionRangeTests
{
    [Theory]
    [InlineData("^3.12.0", "3.12.0", true)]
    [InlineData("^3.12.0", "3.13.5", true)]
    [InlineData("^3.12.0", "4.0.0", false)]
    [InlineData("^3.12.0", "3.11.9", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("^1.2", "1.9.0", true)]
    [InlineData("^1.2", "2.0.0", false)]
    // SML's tilde on a full version allows only that patch (TildeMaxVersion: Patch + 1), unlike npm's "<1.3.0".
    [InlineData("~1.2.3", "1.2.3", true)]
    [InlineData("~1.2.3", "1.2.4", false)]
    [InlineData("~1.2.3", "1.2.9", false)]
    [InlineData("~1.2", "1.2.0", true)]
    [InlineData("~1.2", "1.3.0", false)]
    [InlineData("~1", "1.9.0", true)]
    [InlineData("~1", "2.0.0", false)]
    [InlineData(">=491125", "517554.0.0", true)]
    [InlineData(">=491125", "491124.0.0", false)]
    [InlineData("1.x", "1.5.0", true)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("1.2.x", "1.2.7", true)]
    [InlineData("1.2.x", "1.3.0", false)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData(">=1.0.0 <2.0.0", "1.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false)]
    [InlineData("1.0.0 - 2.0.0", "2.0.0", true)]
    [InlineData("1.0.0 - 2.0.0", "2.0.1", false)]
    [InlineData("1.0.0 - 2", "2.9.0", true)]
    [InlineData("1.0.0 - 2", "3.0.0", false)]
    [InlineData("1.0.0 || ^3.0.0", "3.1.0", true)]
    [InlineData("1.0.0 || ^3.0.0", "2.0.0", false)]
    // SML semantics: prereleases are not excluded from ranges (unlike node-semver).
    [InlineData("^1.0.0", "1.5.0-dev.1", true)]
    [InlineData(">=1.1.0-dev.2", "1.1.0-dev.1", false)]
    [InlineData(">=1.1.0-dev.2", "1.1.0-dev.10", true)]
    public void Matches(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Parse(range).Matches(SemVersion.Parse(version)));

    [Theory]
    [InlineData("")]
    [InlineData(">= 1.0.0")]   // SML does not allow a space after the operator
    [InlineData("1.0.0 -")]
    [InlineData("- 1.0.0")]
    [InlineData("1.0.0 - 2.0.0 - 3.0.0")]
    [InlineData(">x")]
    [InlineData("x.1")]
    [InlineData("garbage")]
    public void RejectsInvalid(string range) => Assert.False(VersionRange.TryParse(range, out _, out _));

    [Fact]
    public void DefaultRemoteRangeRequiresEqualVersionsBothWays()
    {
        // SML checks client against server's ">= own" range and server against client's, so builds must be equal.
        var a = SemVersion.Parse("1.1.0-dev.1");
        var b = SemVersion.Parse("1.1.0-dev.2");
        Assert.False(VersionRange.AtLeast(b).Matches(a) && VersionRange.AtLeast(a).Matches(b));
        Assert.True(VersionRange.AtLeast(a).Matches(a));
    }
}
