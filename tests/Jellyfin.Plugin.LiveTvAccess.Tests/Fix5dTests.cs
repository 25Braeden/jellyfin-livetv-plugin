using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 5d: open-token media source, cross-tuner sharing, hidden upstream URLs.</summary>
public class Fix5dTests
{
    private const string Upstream = "http://provider.example/live/user/secret/123.ts";

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    private static string SourceOf(MediaBrowser.Controller.LiveTv.LiveTvChannel channel) => JellyfinIds.M3uMediaSourceId(channel.ExternalId)!;

    private static MediaSourceInfo LiveSource(string openToken) => new()
    {
        Id = "4ddd999f02971aec37a820d93a68cb59",
        Path = Upstream,
        EncoderPath = Upstream,
        OpenToken = openToken,
        TranscodingUrl = "/videos/x/master.m3u8?a=b",
        RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = "x", ["Authorization"] = "Basic c2VjcmV0" },
    };

    // ---- open-token media source ----

    [Fact]
    public async Task CraftedToken_WithAnotherChannelsMediaSource_IsDenied()
    {
        var w = new World();
        var crafted = $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{w.Tuner1Channel.Id:N}_{SourceOf(w.Tuner2Channel)}";
        var r = await w.RunAt(World.Principal(w.UserA.Id), Args(("openToken", crafted)), "MediaInfo", "OpenLiveStream");
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
    }

    [Fact]
    public async Task OwnToken_AllowedSourceForms()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var ch = w.Tuner1Channel.Id.ToString("N");
        foreach (var token in new[]
        {
            $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{ch}_{SourceOf(w.Tuner1Channel)}",
            $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{ch}_{ch}",
            $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{ch}",
        })
        {
            Assert.Null((await w.RunAt(a, Args(("openToken", token)), "MediaInfo", "OpenLiveStream")).Status);
        }

        Assert.Equal(403, (await w.RunAt(a, Args(("openToken", $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{ch}_{Guid.NewGuid():N}")), "MediaInfo", "OpenLiveStream")).Status);
    }

    // ---- accidental cross-tuner sharing (same stream URL on both tuners) ----

    [Fact]
    public async Task SameUrlOnBothTuners_StreamOpenOnOtherTuner_OpeningIsDenied()
    {
        var w = new World();
        var twin = w.AddChannel(World.Tuner2Url, w.Tuner1Channel.Name!); // B's tuner-2 channel with A's exact URL
        Assert.Equal(SourceOf(w.Tuner1Channel), SourceOf(twin));
        var a = World.Principal(w.UserA.Id);

        // Nothing open yet: A opens normally.
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Tuner1Channel.Id)), "MediaInfo", "GetPostedPlaybackInfo", "POST", null, "itemId")).Status);

        // B's tuner-2 stream of that URL is open: Jellyfin would hand it to A. Opening/streaming is denied...
        w.LiveStreams[JellyfinIds.LiveTvStreamId(SourceOf(twin))] = "host2";
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Tuner1Channel.Id)), "MediaInfo", "GetPostedPlaybackInfo", "POST", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("openToken", w.TokenFor(w.Tuner1Channel))), "MediaInfo", "OpenLiveStream")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Tuner1Channel.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);

        // ...but browsing A's own channel is not.
        Assert.Null((await w.RunAt(a, Args(("channelId", w.Tuner1Channel.Id)), "LiveTv", "GetChannel", "GET", null, "channelId")).Status);

        // If the open stream is on A's own tuner, sharing is fine.
        w.LiveStreams[JellyfinIds.LiveTvStreamId(SourceOf(twin))] = "host1";
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Tuner1Channel.Id)), "MediaInfo", "GetPostedPlaybackInfo", "POST", null, "itemId")).Status);
    }

    // ---- hidden upstream URLs ----

    [Fact]
    public async Task PlaybackInfo_LiveSources_AreScrubbedForNonAdmins_OnACopy()
    {
        var w = new World();
        var original = new PlaybackInfoResponse { MediaSources = [LiveSource(w.TokenFor(w.Tuner1Channel))] };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", new ObjectResult(original) { StatusCode = 200 });

        var scrubbed = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<ObjectResult>(result).Value);
        var source = Assert.Single(scrubbed.MediaSources);
        Assert.True(string.IsNullOrEmpty(source.Path));
        Assert.True(string.IsNullOrEmpty(source.EncoderPath));
        Assert.Empty(source.RequiredHttpHeaders);
        Assert.Equal("/videos/x/master.m3u8?a=b", source.TranscodingUrl);
        Assert.Equal(w.TokenFor(w.Tuner1Channel), source.OpenToken);
        Assert.Equal(200, ((ObjectResult)result!).StatusCode);

        // The server's own object is untouched.
        Assert.Equal(Upstream, original.MediaSources[0].Path);
    }

    [Fact]
    public async Task LiveStreamResponse_IsScrubbed()
    {
        var w = new World();
        var live = LiveSource(string.Empty);
        live.LiveStreamId = World.Tuner1StreamId;
        var args = new Dictionary<string, object?> { ["openToken"] = w.TokenFor(w.Tuner1Channel) };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "OpenLiveStream", new ObjectResult(new LiveStreamResponse(live)), args);
        Assert.True(string.IsNullOrEmpty(((LiveStreamResponse)((ObjectResult)result!).Value!).MediaSource.Path));
    }

    [Fact]
    public async Task Admins_SeeRealValues_SameObject()
    {
        var w = new World();
        var original = new PlaybackInfoResponse { MediaSources = [LiveSource(w.TokenFor(w.Tuner1Channel))] };
        var result = await w.RunWithResult(World.Principal(w.Admin.Id), "MediaInfo", "GetPostedPlaybackInfo", new ObjectResult(original));
        Assert.Same(original, ((ObjectResult)result!).Value);
        Assert.Equal(Upstream, original.MediaSources[0].Path);
    }

    [Fact]
    public async Task ItemDtos_ForLiveTvItems_AreScrubbed_LibraryItemsUntouched()
    {
        var w = new World();
        var channelDto = new BaseItemDto { Id = w.Tuner1Channel.Id, Type = Jellyfin.Data.Enums.BaseItemKind.TvChannel, MediaSources = [new MediaSourceInfo { Path = Upstream }] };
        var movieDto = new BaseItemDto { Id = w.Movie.Id, Type = Jellyfin.Data.Enums.BaseItemKind.Movie, Path = "/media/m.mp4", MediaSources = [new MediaSourceInfo { Path = "/media/m.mp4" }] };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "Items", "GetItems", new ObjectResult(new QueryResult<BaseItemDto>([channelDto, movieDto])));
        var items = ((QueryResult<BaseItemDto>)((ObjectResult)result!).Value!).Items;
        Assert.True(string.IsNullOrEmpty(items[0].MediaSources![0].Path));
        Assert.Equal("/media/m.mp4", items[1].MediaSources![0].Path);

        var onlyMovie = new QueryResult<BaseItemDto>([movieDto]);
        var unchanged = await w.RunWithResult(World.Principal(w.UserA.Id), "Items", "GetItems", new ObjectResult(onlyMovie));
        Assert.Same(onlyMovie, ((ObjectResult)unchanged!).Value);
    }

    [Fact]
    public async Task Sessions_NowPlayingLiveSources_AreScrubbed()
    {
        var w = new World();
        var session = new SessionInfoDto
        {
            NowPlayingItem = new BaseItemDto { Type = Jellyfin.Data.Enums.BaseItemKind.TvChannel, MediaSources = [new MediaSourceInfo { Path = Upstream }] },
        };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "Session", "GetSessions", new ObjectResult(new[] { session }));
        var sessions = (IEnumerable<SessionInfoDto>)((ObjectResult)result!).Value!;
        Assert.True(string.IsNullOrEmpty(sessions.Single().NowPlayingItem!.MediaSources![0].Path));
        Assert.Equal(Upstream, session.NowPlayingItem.MediaSources[0].Path);
    }

    [Fact]
    public async Task LiveTvConfig_TunerAndListingSecrets_AreBlankedForNonAdmins()
    {
        var w = new World();
        LiveTvOptions Make() => new()
        {
            TunerHosts = [new TunerHostInfo { Id = "h1", Type = "m3u", Url = "http://provider.example/get.php?username=u&password=p", UserAgent = "ua" }],
            ListingProviders = [new ListingsProviderInfo { Id = "l1", Type = "xmltv", Path = "http://provider.example/xmltv.php?username=u&password=p", Username = "u", Password = "p", ListingsId = "x" }],
        };
        var args = new Dictionary<string, object?> { ["key"] = "livetv" };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "Configuration", "GetNamedConfiguration", new ObjectResult(Make()), args);
        var cfg = (LiveTvOptions)((ObjectResult)result!).Value!;
        Assert.Single(cfg.TunerHosts);
        Assert.True(string.IsNullOrEmpty(cfg.TunerHosts[0].Url));
        Assert.True(string.IsNullOrEmpty(cfg.ListingProviders[0].Path));
        Assert.True(string.IsNullOrEmpty(cfg.ListingProviders[0].Username));
        Assert.True(string.IsNullOrEmpty(cfg.ListingProviders[0].Password));

        var adminCfg = Make();
        var adminResult = await w.RunWithResult(World.Principal(w.Admin.Id), "Configuration", "GetNamedConfiguration", new ObjectResult(adminCfg), args);
        Assert.Same(adminCfg, ((ObjectResult)adminResult!).Value);
    }

    [Fact]
    public async Task NonLiveTvResponses_AreNotCopied()
    {
        var w = new World();
        var info = new PlaybackInfoResponse { MediaSources = [new MediaSourceInfo { Path = "/media/m.mp4", Id = "m" }] };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", new ObjectResult(info));
        Assert.Same(info, ((ObjectResult)result!).Value);
    }
}
