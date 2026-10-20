using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 5b: anonymous callers, legacy "params", play session reuse.</summary>
public class Fix5bTests
{
    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    private static System.Security.Claims.ClaimsPrincipal Anonymous => World.Principal(null, authenticated: false);

    // ---- anonymous callers ----

    [Fact]
    public async Task Anonymous_StreamOfAnyLiveTvChannel_IsDenied_RouteQueryParams()
    {
        var w = new World();
        foreach (var channel in new[] { w.Tuner1Channel, w.Tuner2Channel })
        {
            Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", channel.Id), ("static", true)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
            Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", channel.Id)), "Audio", "GetAudioStream", "GET", null, "itemId")).Status);
            Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", channel.Id)), "DynamicHls", "GetMasterHlsVideoPlaylist", "GET", null, "itemId")).Status);
        }

        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", w.Movie.Id), ("liveStreamId", World.Tuner1StreamId)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", w.Movie.Id), ("params", World.Params(22, World.Tuner2StreamId))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", w.Tuner1Program.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", w.Recording.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", Guid.NewGuid())), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
    }

    [Fact]
    public async Task Anonymous_Denials_UseTheGenericMessage()
    {
        var w = new World();
        var body = (await w.RunAt(Anonymous, Args(("itemId", w.Tuner2Channel.Id)), "Videos", "GetVideoStream", "GET", null, "itemId")).Body;
        Assert.Equal(AccessPolicy.DeniedMessage, body);
    }

    [Fact]
    public async Task Anonymous_OrdinaryRequests_AreUnaffected()
    {
        var w = new World();
        Assert.Null((await w.RunAt(Anonymous, Args(), "System", "GetPublicSystemInfo", "GET")).Status);
        Assert.Null((await w.RunAt(Anonymous, Args(("request", new { Username = "A", Pw = Guid.NewGuid().ToString("N") })), "User", "AuthenticateUserByName")).Status);
        Assert.Null((await w.RunAt(Anonymous, Args(), "Branding", "GetBrandingOptions", "GET")).Status);
        Assert.Null((await w.RunAt(Anonymous, Args(("itemId", w.Movie.Id), ("static", true)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(Anonymous, Args(("itemId", w.Song.Id)), "Audio", "GetAudioStream", "GET", null, "itemId")).Status);
    }

    [Fact]
    public async Task Anonymous_Images_StayReadable_IncludingChannelLogos()
    {
        // Accepted (user decision): clients load images without auth; logos and programme art are metadata only.
        var w = new World();
        foreach (var id in new[] { w.Movie.Id, w.Tuner1Channel.Id, w.Tuner2Channel.Id, w.Tuner2Program.Id })
        {
            Assert.Null((await w.RunAt(Anonymous, Args(("itemId", id), ("imageType", "Primary")), "Image", "GetItemImage", "GET", null, "itemId", "imageType")).Status);
        }
    }

    [Fact]
    public async Task Anonymous_ServerInternalBufferUrls_AreUnchanged_UntilFix6()
    {
        // Jellyfin's own ffmpeg reads these without auth (LiveTvController.cs:1162, :1189).
        var w = new World();
        Assert.Null((await w.RunAt(Anonymous, Args(("streamId", Guid.NewGuid().ToString("N")), ("container", "ts")), "LiveTv", "GetLiveStreamFile", "GET", null, "streamId", "container")).Status);
        Assert.Null((await w.RunAt(Anonymous, Args(("recordingId", Guid.NewGuid().ToString("N"))), "LiveTv", "GetLiveRecordingFile", "GET", null, "recordingId")).Status);
    }

    [Fact]
    public async Task Anonymous_OtherLiveTvControllerActions_AreDenied()
    {
        var w = new World();
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(), "LiveTv", "GetLiveTvChannels", "GET")).Status);
    }

    // ---- legacy "params" ----

    [Fact]
    public async Task ParamsField22_LiveStreamId_IsResolvedAndEnforced()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(22, World.Tuner2StreamId))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(22, World.Tuner1StreamId))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(22, $"{JellyfinIds.LiveTvProviderPrefix}_{Guid.NewGuid():N}_{Guid.NewGuid():N}"))), "DynamicHls", "GetVariantHlsVideoPlaylist", "GET", null, "itemId")).Status);
    }

    [Fact]
    public async Task ParamsFields_MediaSourceAndOtherFields_AreInspected()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(2, w.Tuner2Channel.Id.ToString("N")))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(2, w.Movie.Id.ToString("N")) + ";extra;fields")), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
    }

    // ---- play session reuse ----

    [Fact]
    public async Task PlaySessionOfALiveTvJob_IsCheckedAgainstTheCallersTuner()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var otherTunerJob = w.AddJob(World.Tuner2StreamId);
        var ownTunerJob = w.AddJob(World.Tuner1StreamId);
        var movieJob = w.AddJob(null);

        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("playSessionId", otherTunerJob)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("params", World.Params(20, otherTunerJob))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(a, Args(("itemId", w.Movie.Id), ("body", new { PlaySessionId = otherTunerJob })), "Playstate", "ReportPlaybackProgress")).Status);
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Movie.Id), ("playSessionId", ownTunerJob)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Movie.Id), ("playSessionId", movieJob)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Null((await w.RunAt(a, Args(("itemId", w.Movie.Id), ("playSessionId", Guid.NewGuid().ToString("N"))), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        Assert.Equal(403, (await w.RunAt(Anonymous, Args(("itemId", w.Movie.Id), ("playSessionId", ownTunerJob)), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
    }

    [Fact]
    public async Task PlaySessionIdThatIsNotGuidShaped_IsStillLookedUp()
    {
        // Jellyfin accepts any client-chosen string as a play session id.
        var w = new World();
        w.Jobs["custom-session"] = new MediaBrowser.Controller.MediaEncoding.TranscodingJob(Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaBrowser.Controller.MediaEncoding.TranscodingJob>.Instance)
        {
            PlaySessionId = "custom-session",
            LiveStreamId = World.Tuner2StreamId,
        };
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserA.Id), Args(("itemId", w.Movie.Id), ("playSessionId", "CUSTOM-SESSION")), "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
    }
}
