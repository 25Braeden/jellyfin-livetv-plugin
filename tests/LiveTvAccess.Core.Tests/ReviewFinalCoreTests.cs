using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

/// <summary>Final review: the static decision comes only from Jellyfin's effective stream parameters.</summary>
public class ReviewFinalCoreTests
{
    private static string Params(params (int Index, string Value)[] fields)
    {
        var values = new string[29];
        foreach (var (index, value) in fields)
        {
            values[index] = value;
        }

        return string.Join(';', values);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(null, "true", true)]
    [InlineData(null, "TRUE", true)]
    [InlineData(null, " true ", true)] // Jellyfin: not static; fail closed
    [InlineData(null, "1", true)]
    [InlineData(null, "yes", true)]
    [InlineData(null, "false", false)]
    [InlineData(null, "False", false)]
    [InlineData(true, "false", false)] // params overrides the query value, as in Jellyfin
    [InlineData(true, "  ", true)] // blank field: no override
    [InlineData(true, "", true)]
    public void IsStatic(bool? staticArgument, string? field3, bool expected)
    {
        Assert.Equal(expected, StaticStreamRequest.IsStatic(staticArgument, field3 is null ? null : Params((3, field3))));
    }

    [Fact]
    public void IsStatic_ShortParams_NoField3()
    {
        Assert.False(StaticStreamRequest.IsStatic(null, ";;"));
        Assert.True(StaticStreamRequest.IsStatic(null, ";;;true"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("S1", null, "S1")]
    [InlineData("  ", null, null)]
    [InlineData(null, "S2", "S2")]
    [InlineData("S1", "S2", "S2")] // params field 22 wins
    [InlineData("S1", " ", "S1")] // blank field: no override
    public void EffectiveLiveStreamId(string? argument, string? field22, string? expected)
    {
        Assert.Equal(expected, StaticStreamRequest.EffectiveLiveStreamId(argument, field22 is null ? null : Params((22, field22))));
    }
}
