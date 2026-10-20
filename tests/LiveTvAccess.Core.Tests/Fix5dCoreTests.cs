using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix5dCoreTests
{
    private const string P = "e2329f4997b378e64ccf8fa396deb76e";
    private const string Channel = "fe006602fc01342e1f5d63b0b0f9f40a";
    private const string Source = "4ddd999f02971aec37a820d93a68cb59";
    private const string NewsExternalId = "m3u_dffda8d9d52d92600249def455be1dbc" + Source;

    [Fact]
    public void M3uMediaSourceId_IsTheExternalIdSuffix()
    {
        Assert.Equal(Source, JellyfinIds.M3uMediaSourceId(NewsExternalId));
        Assert.Null(JellyfinIds.M3uMediaSourceId("hdhr_123"));
        Assert.Null(JellyfinIds.M3uMediaSourceId("m3u_short"));
        Assert.Null(JellyfinIds.M3uMediaSourceId(null));
    }

    [Fact]
    public void LiveTvStreamId_MatchesTheServer()
    {
        // Seen on the local server for Test News.
        Assert.Equal(P + "_af999c25a00715699361240d4c6c7a53_" + Source, JellyfinIds.LiveTvStreamId(Source));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(Channel, true)]
    [InlineData("FE006602FC01342E1F5D63B0B0F9F40A", true)]
    [InlineData(Source, true)]
    [InlineData("4DDD999F02971AEC37A820D93A68CB59", true)]
    [InlineData("5ec6f96467952921c6e26b7f28e0b318", false)]
    [InlineData("anything", false)]
    public void IsAllowedOpenTokenSource(string? sourceId, bool expected)
    {
        Assert.Equal(expected, JellyfinIds.IsAllowedOpenTokenSource(sourceId, Channel, Source));
    }

    [Fact]
    public void IsAllowedOpenTokenSource_UnknownChannelSource_OnlyEmptyOrChannelId()
    {
        Assert.True(JellyfinIds.IsAllowedOpenTokenSource(null, Channel, null));
        Assert.False(JellyfinIds.IsAllowedOpenTokenSource(Source, Channel, null));
    }

    [Theory]
    [InlineData(P + "_LiveTvChannel_" + Channel + "_" + Source, Channel, Source)]
    [InlineData(P + "_LiveTvChannel_" + Channel, Channel, null)]
    [InlineData(P + "_LiveTvChannel_" + Channel + "_a_b", Channel, "a_b")]
    public void TrySplitLiveTvOpenToken(string token, string channel, string? source)
    {
        Assert.True(ProviderString.TrySplitLiveTvOpenToken(token, out var c, out var s));
        Assert.Equal(channel, c);
        Assert.Equal(source, s);
    }

    [Fact]
    public void TrySplitLiveTvOpenToken_RejectsOtherShapes()
    {
        Assert.False(ProviderString.TrySplitLiveTvOpenToken(P + "_af999c25a00715699361240d4c6c7a53_" + Source, out _, out _));
        Assert.False(ProviderString.TrySplitLiveTvOpenToken("0123456789abcdef0123456789abcdef_LiveTvChannel_" + Channel, out _, out _));
        Assert.False(ProviderString.TrySplitLiveTvOpenToken(null, out _, out _));
    }
}
