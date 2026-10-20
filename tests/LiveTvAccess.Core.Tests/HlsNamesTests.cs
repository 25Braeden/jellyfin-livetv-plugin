using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class HlsNamesTests
{
    private const string Job = "f182b233fa90302addc4c6e75df854e8";

    [Theory]
    [InlineData(Job, true)]
    [InlineData(Job + "0", true)]
    [InlineData(Job + "-1", true)]
    [InlineData("main", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../etc", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a%2Fb", false)]
    [InlineData("%2e%2e", false)]
    [InlineData("name.ts", false)]
    [InlineData(" name", false)]
    [InlineData("name\n", false)]
    public void IsSafeName(string? value, bool expected)
    {
        Assert.Equal(expected, HlsNames.IsSafeName(value));
    }

    [Fact]
    public void IsSafeName_RejectsOverlongNames()
    {
        Assert.False(HlsNames.IsSafeName(new string('a', 129)));
        Assert.True(HlsNames.IsSafeName(new string('a', 128)));
    }

    [Theory]
    [InlineData(Job + "0", true)]
    [InlineData(Job + "123456789", true)]
    [InlineData(Job + "-1", true)]
    [InlineData(Job, false)]
    [InlineData(Job + "1234567890", false)]
    [InlineData(Job + "x0", false)]
    [InlineData(Job + "-2", false)]
    [InlineData("other" + "0", false)]
    [InlineData("f182b233fa90302addc4c6e75df854e", false)]
    [InlineData("F182B233FA90302ADDC4C6E75DF854E80", false)]
    public void IsSegmentOf_ExactOnly(string segmentId, bool expected)
    {
        Assert.Equal(expected, HlsNames.IsSegmentOf(Job, segmentId));
    }

    [Fact]
    public void CandidateJobNames_StripsTrailingDigitsOrInitSuffix()
    {
        Assert.Equal([Job, Job[..^1]], HlsNames.CandidateJobNames(Job + "0"));
        Assert.Equal([Job], HlsNames.CandidateJobNames(Job + "-1"));
        Assert.Empty(HlsNames.CandidateJobNames("abc"));
        Assert.Equal(["12", "1"], HlsNames.CandidateJobNames("123"));
    }
}
