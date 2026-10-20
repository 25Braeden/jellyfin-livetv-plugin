using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using LiveTvAccess.Core;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 5: default-deny filter.</summary>
public class Fix5Tests
{
    private const string LiveTv = "LiveTv";

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    // A body DTO of an arbitrary (non-Jellyfin.Api) type, nested two levels deep.
    public sealed class QueueItem
    {
        public Guid Id { get; set; }
    }

    public sealed class NestedBody
    {
        public string? Note { get; set; }

        public Inner? Inner { get; set; }
    }

    public sealed class Inner
    {
        public List<QueueItem> Queue { get; set; } = new();
    }

    public sealed class StreamBody
    {
        public string? LiveStreamId { get; set; }

        public string? MediaSourceId { get; set; }

        public string? PlaySessionId { get; set; }

        public DeviceProfile? DeviceProfile { get; set; }
    }

    // ---- allowlist ----

    [Theory]
    [InlineData("GetLiveTvInfo")]
    [InlineData("GetLiveTvChannels")]
    [InlineData("GetLiveTvPrograms")]
    [InlineData("GetRecommendedPrograms")]
    [InlineData("GetGuideInfo")]
    public async Task AllowlistedListingActions_WorkForAMappedUser(string action)
    {
        var w = new World();
        Assert.Null((await w.RunAt(World.Principal(w.UserA.Id), Args(("userId", w.UserA.Id)), LiveTv, action, "GET")).Status);
    }

    [Fact]
    public async Task AllowlistedItemActions_WorkForOwnTuner_DenyOtherTuner()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Null((await w.RunAt(a, Args(("channelId", w.Tuner1Channel.Id)), LiveTv, "GetChannel", "GET", null, "channelId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("channelId", w.Tuner2Channel.Id)), LiveTv, "GetChannel", "GET", null, "channelId")).Status);
        Assert.Null((await w.RunAt(a, Args(("programId", w.Tuner1Program.Id.ToString("N"))), LiveTv, "GetProgram", "GET", null, "programId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("programId", w.Tuner2Program.Id.ToString("N"))), LiveTv, "GetProgram", "GET", null, "programId")).Status);
        // Jellyfin's GetProgramsDto has a UserId (bound to the caller since the final review).
        var body = new ReviewFinalUserBindingTests.ProgramsBody { ChannelIds = [w.Tuner1Channel.Id] };
        Assert.Null((await w.RunAt(a, Args(("body", body)), LiveTv, "GetPrograms")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("body", new ReviewFinalUserBindingTests.ProgramsBody { ChannelIds = [w.Tuner1Channel.Id, w.Tuner2Channel.Id] })), LiveTv, "GetPrograms")).Status);
    }

    [Theory]
    [InlineData("CreateTimer")]
    [InlineData("GetTimers")]
    [InlineData("CreateSeriesTimer")]
    [InlineData("GetRecordings")]
    [InlineData("DeleteRecording")]
    [InlineData("GetLiveRecordingFile")]
    [InlineData("GetLiveStreamFile")]
    [InlineData("AddTunerHost")]
    [InlineData("ResetTuner")]
    [InlineData("AddListingProvider")]
    [InlineData("SetChannelMapping")]
    [InlineData("DiscoverTuners")]
    [InlineData("SomeFutureAction")]
    public async Task NonAllowlistedLiveTvActions_AreDeniedForNonAdmins_AllowedForAdmins(string action)
    {
        var w = new World();
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserA.Id), Args(), LiveTv, action)).Status);
        Assert.Null((await w.RunAt(World.Principal(w.Admin.Id), Args(), LiveTv, action)).Status);
        Assert.Null((await w.RunAt(World.Principal(null, isApiKey: true), Args(), LiveTv, action)).Status);
    }

    [Fact]
    public async Task UserWithNoTuner_IsDeniedTheLiveTvListings()
    {
        var w = new World();
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserC.Id), Args(), LiveTv, "GetLiveTvChannels", "GET")).Status);
    }

    // ---- stream open / close ----

    [Fact]
    public async Task OpenAndClose_OwnTunerOnly_AndATargetIsRequired()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Null((await w.RunAt(a, Args(("openToken", w.TokenFor(w.Tuner1Channel))), "MediaInfo", "OpenLiveStream")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("openToken", w.TokenFor(w.Tuner2Channel))), "MediaInfo", "OpenLiveStream")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("openToken", null), ("openLiveStreamDto", null)), "MediaInfo", "OpenLiveStream")).Status);
        Assert.Null((await w.RunAt(a, Args(("liveStreamId", World.Tuner1StreamId)), "MediaInfo", "CloseLiveStream")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("liveStreamId", World.Tuner2StreamId)), "MediaInfo", "CloseLiveStream")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("liveStreamId", "")), "MediaInfo", "CloseLiveStream")).Status);
    }

    // ---- argument shapes ----

    [Fact]
    public async Task NestedBodyProperties_OfAnyType_AreInspected()
    {
        var w = new World();
        var body = new NestedBody { Note = "hi", Inner = new Inner { Queue = [new QueueItem { Id = w.Movie.Id }, new QueueItem { Id = w.Tuner2Channel.Id }] } };
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Args(("request", body))));
        body.Inner.Queue.RemoveAt(1);
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Args(("request", body))));
    }

    [Fact]
    public async Task ArrayParameters_GuidArray_ListOfGuid_StringArray_AreInspected()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, await w.RunFilter(a, Args(("itemIds", new[] { w.Movie.Id, w.Tuner2Channel.Id }))));
        Assert.Equal(403, await w.RunFilter(a, Args(("ids", new List<Guid> { w.Tuner2Channel.Id }))));
        Assert.Equal(403, await w.RunFilter(a, Args(("anything", new[] { "x", w.Tuner2Channel.Id.ToString("D") }))));
        Assert.Null(await w.RunFilter(a, Args(("itemIds", new[] { w.Movie.Id, w.Tuner1Channel.Id }))));
    }

    [Fact]
    public async Task SameForbiddenId_ViaRoute_Query_OrBody_IsDenied()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var id = w.Tuner2Channel.Id;
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("parentId", id)), "Items", "GetItems", "GET")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("body", new { ItemId = id })), "Playstate", "ReportPlaybackStart")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("x", id.ToString("N"))), "Anything", "Anything", "GET")).Status);
    }

    [Fact]
    public async Task MixedAllowedAndForbiddenIds_InOneRequest_AreDenied()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, await w.RunFilter(a, Args(("itemId", w.Tuner1Channel.Id), ("liveStreamId", World.Tuner2StreamId))));
        Assert.Equal(403, await w.RunFilter(a, Args(("itemId", w.Movie.Id), ("channelId", w.Tuner2Channel.Id))));
        Assert.Null(await w.RunFilter(a, Args(("itemId", w.Tuner1Channel.Id), ("liveStreamId", World.Tuner1StreamId), ("songId", w.Song.Id))));
    }

    [Fact]
    public async Task UnresolvableValues_RouteAndTokenShapedDenied_OthersIgnored()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", Guid.NewGuid())), "MediaInfo", "GetPostedPlaybackInfo", "POST", null, "itemId")).Status);
        Assert.Equal(403, await w.RunFilter(a, Args(("liveStreamId", $"{JellyfinIds.LiveTvProviderPrefix}_{Guid.NewGuid():N}_{Guid.NewGuid():N}"))));
        Assert.Equal(403, await w.RunFilter(a, Args(("whatever", "0123456789abcdef0123456789abcdef_unknown"))));

        // A play session id, a media source id and an HLS playlist id are GUID-shaped but not items: ignored.
        Assert.Null((await w.RunAt(
            a,
            Args(("itemId", w.Movie.Id), ("playlistId", Guid.NewGuid().ToString("N")), ("playSessionId", Guid.NewGuid().ToString("N")), ("mediaSourceId", Guid.NewGuid().ToString("N"))),
            "DynamicHls",
            "GetHlsVideoSegment",
            "GET",
            null,
            "itemId",
            "playlistId")).Status);
    }

    [Fact]
    public async Task MalformedLiveTvToken_IsDenied_WithoutLookingUpTheEmptyId()
    {
        // Regression (seen live): the token's item id didn't parse, the empty id was looked up, Jellyfin threw -> 500.
        var w = new World();
        var token = $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_not-a-guid";
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserA.Id), Args(("openToken", token)), "MediaInfo", "OpenLiveStream")).Status);
    }

    [Fact]
    public async Task LookupThatThrows_IsDenied_RouteOrNot()
    {
        // Regression (seen live): Jellyfin's placeholder row throws on load -> 500. Unclassifiable = deny.
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var placeholder = Guid.Parse("00000000000000000000000000000001");
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", placeholder)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, await w.RunFilter(a, Args(("parentId", placeholder))));
    }

    [Fact]
    public async Task OpenStreamOfANonTunerProvider_IsNotLiveTv()
    {
        var w = new World();
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Args(("liveStreamId", World.OtherProviderStreamId))));
    }

    [Fact]
    public async Task Recordings_AreDeniedForNonAdmins_FinishedAndActive()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Recording.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(World.Principal(w.Admin.Id), Args(("itemId", w.Recording.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);

        var active = w.AddItem(new MediaBrowser.Controller.Entities.Video { Name = "in progress", Path = "/elsewhere/now.ts" });
        w.ActiveRecordingPaths.Add("/elsewhere/now.ts");
        Assert.Equal(403, await w.RunFilter(a, Args(("itemId", active.Id))));
    }

    [Fact]
    public async Task OversizedBody_IsDenied_FailClosed()
    {
        var w = new World();
        var huge = Enumerable.Range(0, Filters.TargetCollector.MaxNodes + 10).Select(i => "x" + i).ToArray();
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Args(("values", huge))));
    }

    // ---- KnownIssue shapes (also in ReviewReproTests) ----

    [Fact]
    public async Task TimerBody_ForOtherTunersChannel_IsDenied_EvenOutsideLiveTvController()
    {
        var w = new World();
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Args(("timerInfo", new TimerInfoDto { ChannelId = w.Tuner2Channel.Id, ProgramId = w.Tuner2Program.Id.ToString("N") }))));
    }

    // ---- regressions: normal library use ----

    [Theory]
    [InlineData("A")]
    [InlineData("C")]
    public async Task LibraryPlaybackAndBrowsing_StillWork(string who)
    {
        var w = new World();
        var user = who == "A" ? w.UserA : w.UserC;
        if (who == "C")
        {
            user.SetPermission(PermissionKind.EnableLiveTvAccess, true);
        }

        var p = World.Principal(user.Id);
        var profile = new DeviceProfile { Name = "Test", DirectPlayProfiles = [new DirectPlayProfile { Container = "mp4,mkv", VideoCodec = "h264,hevc" }] };

        // Movie: details, playback info (with a device profile and session ids), stream, HLS segment, image.
        Assert.Null((await w.RunAt(p, Args(("userId", user.Id), ("itemId", w.Movie.Id)), "UserLibrary", "GetItem", "GET", null, "userId", "itemId")).Status);
        Assert.Null((await w.RunAt(p, Args(("itemId", w.Movie.Id), ("body", new StreamBody { DeviceProfile = profile, MediaSourceId = w.Movie.Id.ToString("N"), PlaySessionId = Guid.NewGuid().ToString("N") })), "MediaInfo", "GetPostedPlaybackInfo", "POST", null, "itemId")).Status);
        Assert.Null((await w.RunAt(p, Args(("itemId", w.Movie.Id), ("mediaSourceId", w.Movie.Id.ToString("N")), ("static", true)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(p, Args(("itemId", w.Movie.Id), ("playlistId", Guid.NewGuid().ToString("N")), ("segmentId", 0)), "DynamicHls", "GetHlsVideoSegment", "GET", null, "itemId", "playlistId", "segmentId")).Status);
        Assert.Null((await w.RunAt(p, Args(("itemId", w.Movie.Id), ("imageType", "Primary")), "Image", "GetItemImage", "GET", null, "itemId", "imageType")).Status);

        // Music, browsing a folder, Users/{id}/Items, search.
        Assert.Null((await w.RunAt(p, Args(("itemId", w.Song.Id), ("container", new[] { "mp3", "aac" })), "UniversalAudio", "GetUniversalAudioStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(p, Args(("userId", user.Id), ("parentId", w.LibraryFolder.Id), ("recursive", true)), "Items", "GetItemsByUserIdLegacy", "GET", null, "userId")).Status);
        Assert.Null((await w.RunAt(p, Args(("userId", user.Id), ("searchTerm", "Test Movie"), ("includeItemTypes", new[] { "Movie", "Audio" })), "Items", "GetItems", "GET")).Status);

        // Playback reporting for a movie.
        Assert.Null((await w.RunAt(p, Args(("body", new { ItemId = w.Movie.Id, PlaySessionId = Guid.NewGuid().ToString("N") })), "Playstate", "ReportPlaybackStart")).Status);
    }

    [Fact]
    public async Task EveryNewDenialReason_UsesTheGenericMessage()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var bodies = new[]
        {
            (await w.RunAt(a, Args(), LiveTv, "CreateTimer")).Body,
            (await w.RunAt(a, Args(("itemId", Guid.NewGuid())), "Videos", "GetVideoStream", "GET", null, "itemId")).Body,
            (await w.RunAt(a, Args(("itemId", w.Recording.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Body,
            (await w.RunAt(a, Args(), "MediaInfo", "OpenLiveStream")).Body,
            (await w.RunAt(a, Args(("channelId", w.Tuner2Channel.Id)), LiveTv, "GetChannel", "GET", null, "channelId")).Body,
        };
        Assert.All(bodies, b => Assert.Equal(AccessPolicy.DeniedMessage, b));
    }
}
