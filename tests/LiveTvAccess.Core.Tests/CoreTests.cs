using System;
using System.Collections.Generic;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class CoreTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid UserC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static UserTunerMap Map() => new(
        ["tuner1", "Tuner2"],
        [new KeyValuePair<Guid, string>(UserA, "tuner1"), new KeyValuePair<Guid, string>(UserB, "TUNER2")]);

    [Fact]
    public void ForTuner_NormalizesToLowercaseWithPrefix()
    {
        Assert.Equal("livetv-tuner1", TunerTags.ForTuner(" Tuner1 "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tuner 1")]
    [InlineData("tuner_1")]
    public void NormalizeKey_RejectsInvalidKeys(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => TunerTags.NormalizeKey(key));
    }

    [Theory]
    [InlineData("livetv-tuner1", true)]
    [InlineData("LiveTV-x", true)]
    [InlineData("movies", false)]
    [InlineData(null, false)]
    public void IsManaged_MatchesPrefixOnly(string? tag, bool expected)
    {
        Assert.Equal(expected, TunerTags.IsManaged(tag));
    }

    [Fact]
    public void GetBlockedTags_BlocksEveryOtherTuner()
    {
        var map = Map();
        Assert.Equal(["livetv-tuner2", "livetv-unassigned"], map.GetBlockedTags(UserA));
        Assert.Equal(["livetv-tuner1", "livetv-unassigned"], map.GetBlockedTags(UserB));
    }

    [Fact]
    public void GetBlockedTags_UnassignedUserIsDeniedEverything()
    {
        Assert.Null(Map().GetTuner(UserC));
        Assert.Equal(["livetv-tuner1", "livetv-tuner2", "livetv-unassigned"], Map().GetBlockedTags(UserC));
    }

    [Fact]
    public void Constructor_RejectsUnknownTunerAndDuplicateUser()
    {
        Assert.Throws<ArgumentException>(() => new UserTunerMap(
            ["tuner1"], [new KeyValuePair<Guid, string>(UserA, "tuner9")]));
        Assert.Throws<ArgumentException>(() => new UserTunerMap(
            ["tuner1"],
            [new KeyValuePair<Guid, string>(UserA, "tuner1"), new KeyValuePair<Guid, string>(UserA, "tuner1")]));
    }

    // Known values observed on the local Jellyfin 10.11.11 server.
    private const string Tuner1Url = "/tuners/tuner1.m3u";
    private const string Tuner2Url = "/tuners/tuner2.m3u";
    private const string NewsExternalId = "m3u_dffda8d9d52d92600249def455be1dbc4ddd999f02971aec37a820d93a68cb59";
    private const string NewsToken = "e2329f4997b378e64ccf8fa396deb76e_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a_4ddd999f02971aec37a820d93a68cb59";

    private static TunerCatalog Catalog() => new(
        [new KeyValuePair<string, string>("tuner1", Tuner1Url), new KeyValuePair<string, string>("tuner2", Tuner2Url)]);

    [Fact]
    public void JellyfinIds_MatchServer()
    {
        Assert.Equal("e2329f4997b378e64ccf8fa396deb76e", JellyfinIds.LiveTvProviderPrefix);
        Assert.Equal("m3u_dffda8d9d52d92600249def455be1dbc", JellyfinIds.M3uChannelPrefix(Tuner1Url));
    }

    [Theory]
    [InlineData(NewsToken, true, true, "fe006602fc01342e1f5d63b0b0f9f40a")]
    [InlineData("e2329f4997b378e64ccf8fa396deb76e_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a", true, true, "fe006602fc01342e1f5d63b0b0f9f40a")]
    [InlineData("e2329f4997b378e64ccf8fa396deb76e_LiveTvChannel_not-a-guid", false, true, null)]
    [InlineData("e2329f4997b378e64ccf8fa396deb76e_", false, true, null)]
    [InlineData("0123456789abcdef0123456789abcdef_Video_fe006602fc01342e1f5d63b0b0f9f40a", false, false, null)]
    [InlineData(null, false, false, null)]
    public void TryParseLiveTvOpenToken(string? token, bool parsed, bool isLiveTv, string? itemId)
    {
        Assert.Equal(parsed, JellyfinIds.TryParseLiveTvOpenToken(token, out var liveTv, out var id));
        Assert.Equal(isLiveTv, liveTv);
        Assert.Equal(itemId is null ? Guid.Empty : Guid.Parse(itemId), id);
    }

    [Fact]
    public void TunerCatalog_ResolvesChannelsAndUrls()
    {
        var catalog = Catalog();
        Assert.Equal("tuner1", catalog.ResolveChannel(NewsExternalId));
        Assert.Null(catalog.ResolveChannel("m3u_00000000000000000000000000000000abc"));
        Assert.Null(catalog.ResolveChannel("hdhr_123"));
        Assert.Null(catalog.ResolveChannel(null));
        Assert.Equal("tuner2", catalog.ResolveUrl(Tuner2Url));
        Assert.Null(catalog.ResolveUrl("/tuners/other.m3u"));
    }

    [Fact]
    public void AccessPolicy_Decisions()
    {
        var map = Map();
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(UserA, false, false, true), map, "tuner1"));
        Assert.Equal(AccessDecision.DenyOtherTuner, AccessPolicy.Decide(new Caller(UserA, false, false, true), map, "tuner2"));
        Assert.Equal(AccessDecision.DenyUnknownTuner, AccessPolicy.Decide(new Caller(UserA, false, false, true), map, null));
        Assert.Equal(AccessDecision.DenyNoTuner, AccessPolicy.Decide(new Caller(UserC, false, false, true), map, "tuner1"));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(UserC, false, true, true), map, null));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(Guid.Empty, true, false, true), map, null));
        Assert.Equal(AccessDecision.DenyNoTuner, AccessPolicy.Decide(new Caller(Guid.Empty, false, false, true), map, "tuner1"));
    }
}
