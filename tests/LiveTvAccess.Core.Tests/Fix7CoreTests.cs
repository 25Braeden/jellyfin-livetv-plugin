using System;
using System.Collections.Generic;
using System.Linq;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix7CoreTests
{
    private const string Url1 = "http://provider.example:8080/get.php?username=alice&password=s3cret&type=m3u";
    private const string Url2 = "/config/tuners/tuner2.m3u";

    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static TunerCatalog Catalog(params (string Key, string Url)[] tuners)
        => new(tuners.Select(t => new KeyValuePair<string, string>(t.Key, t.Url)));

    private static UserTunerMap Map(TunerCatalog catalog, params (Guid User, string Tuner)[] users)
        => new(catalog.Keys, users.Select(u => new KeyValuePair<Guid, string>(u.User, u.Tuner)));

    private static string Channel(string tunerUrl, string streamUrl)
        => JellyfinIds.M3uChannelPrefix(tunerUrl) + JellyfinIds.Md5Guid(streamUrl);

    [Fact]
    public void Healthy_TwoTunersWithChannels()
    {
        var catalog = Catalog(("t1", Url1), ("t2", Url2));
        var result = TunerHealth.Evaluate(
            catalog,
            2,
            Map(catalog, (UserA, "t1"), (UserB, "t2")),
            [Url1, Url2],
            [Channel(Url1, "http://s/1"), Channel(Url1, "http://s/2"), Channel(Url2, "http://s/3"), "other-provider-id", null]);

        Assert.Empty(result.Problems);
        Assert.Empty(result.Duplicates);
        Assert.Equal([("t1", 2, 1, true), ("t2", 1, 1, true)], result.Tuners.Select(t => (t.Key, t.ChannelCount, t.UserCount, t.InJellyfin)));
    }

    [Fact]
    public void NoTunersConfigured_IsAProblem()
    {
        var catalog = Catalog();
        var result = TunerHealth.Evaluate(catalog, 0, Map(catalog), [Url1], [Channel(Url1, "http://s/1")]);
        Assert.Contains(result.Problems, p => p.Contains("no tuners", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TunerUrlNotAJellyfinTuner_IsAProblem()
    {
        var catalog = Catalog(("t1", Url1), ("t2", Url2));
        var result = TunerHealth.Evaluate(catalog, 2, Map(catalog), [Url1], [Channel(Url1, "http://s/1"), Channel(Url2, "http://s/3")]);
        Assert.Contains(result.Problems, p => p.Contains("t2", StringComparison.Ordinal) && p.Contains("not a Jellyfin M3U tuner", StringComparison.Ordinal));
        Assert.False(result.Tuners.Single(t => t.Key == "t2").InJellyfin);
    }

    [Fact]
    public void TunerWithoutChannels_IsAProblem()
    {
        var catalog = Catalog(("t1", Url1), ("t2", Url2));
        var result = TunerHealth.Evaluate(catalog, 2, Map(catalog), [Url1, Url2], [Channel(Url1, "http://s/1")]);
        Assert.Contains(result.Problems, p => p.Contains("t2", StringComparison.Ordinal) && p.Contains("no channels", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoredTunerEntries_AreAWarning()
    {
        var catalog = Catalog(("t1", Url1));
        var result = TunerHealth.Evaluate(catalog, 2, Map(catalog), [Url1], [Channel(Url1, "http://s/1")]);
        Assert.Empty(result.Problems);
        Assert.Contains(result.Warnings, w => w.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void JellyfinTunerNotInPluginConfig_IsAWarning()
    {
        var catalog = Catalog(("t1", Url1));
        var result = TunerHealth.Evaluate(catalog, 1, Map(catalog, (UserA, "t1")), [Url1, Url2], [Channel(Url1, "http://s/1"), Channel(Url2, "http://s/2")]);
        Assert.Empty(result.Problems);
        Assert.Contains(result.Warnings, w => w.Contains("not configured in this plugin", StringComparison.Ordinal));
    }

    [Fact]
    public void TunerWithoutUsers_IsAWarning()
    {
        var catalog = Catalog(("t1", Url1));
        var result = TunerHealth.Evaluate(catalog, 1, Map(catalog), [Url1], [Channel(Url1, "http://s/1")]);
        Assert.Empty(result.Problems);
        Assert.Contains(result.Warnings, w => w.Contains("t1", StringComparison.Ordinal) && w.Contains("no users", StringComparison.Ordinal));
    }

    [Fact]
    public void SameStreamUrlOnTwoTuners_IsAWarningWithTheDuplicate_NotAProblem()
    {
        var catalog = Catalog(("t1", Url1), ("t2", Url2));
        var shared = "http://cdn.example/live/news.ts?token=abc";
        var result = TunerHealth.Evaluate(
            catalog,
            2,
            Map(catalog, (UserA, "t1"), (UserB, "t2")),
            [Url1, Url2],
            [Channel(Url1, shared), Channel(Url2, shared), Channel(Url1, "http://s/1"), Channel(Url2, "http://s/2")]);

        Assert.Empty(result.Problems);
        var duplicate = Assert.Single(result.Duplicates);
        Assert.Equal(JellyfinIds.Md5Guid(shared), duplicate.SourceId);
        Assert.Equal(["t1", "t2"], duplicate.Tuners);
        Assert.Equal(2, duplicate.ChannelCount);
        Assert.StartsWith("sha256:", duplicate.SourceHash, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("same stream URL", StringComparison.Ordinal));
    }

    [Fact]
    public void SameStreamUrlTwiceOnOneTuner_IsNotADuplicate()
    {
        var catalog = Catalog(("t1", Url1));
        var result = TunerHealth.Evaluate(catalog, 1, Map(catalog, (UserA, "t1")), [Url1], [Channel(Url1, "http://s/1"), Channel(Url1, "http://s/1")]);
        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public void ResultNeverContainsUrlsOrCredentials()
    {
        var catalog = Catalog(("t1", Url1), ("t2", Url2));
        var result = TunerHealth.Evaluate(catalog, 3, Map(catalog), [Url1], []);
        var text = string.Join("\n", result.Problems.Concat(result.Warnings).Concat(result.Tuners.Select(t => t.ToString())));
        foreach (var secret in new[] { "alice", "s3cret", "get.php", "username", "tuner2.m3u", "/config/" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://alice:pw@provider.example:8080/get.php?username=alice&password=s3cret", "http://provider.example:8080")]
    [InlineData("https://Provider.Example/playlist.m3u?token=x", "https://provider.example")]
    [InlineData("/config/tuners/tuner1.m3u", "file")]
    [InlineData("C:\\tuners\\a.m3u", "file")]
    [InlineData("file:///config/a.m3u", "file")]
    [InlineData("rtsp://cam.example/stream", "rtsp://cam.example")]
    [InlineData("not a url", "(unrecognized)")]
    [InlineData("", "(none)")]
    [InlineData(null, "(none)")]
    public void Location_ShowsOnlySchemeAndHost(string? url, string expected)
    {
        Assert.Equal(expected, Redact.Location(url));
    }
}
