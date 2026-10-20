using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 5e: no static (untracked) streams of Live TV channels for non-admins.</summary>
public class Fix5eTests
{
    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);

    private static System.Security.Claims.ClaimsPrincipal Anonymous => World.Principal(null, authenticated: false);

    private static Task<(int? Status, string? Body)> Stream(World w, System.Security.Claims.ClaimsPrincipal who, string controller, string action, params (string, object?)[] args)
        => w.RunAt(who, Args(args), controller, action, "GET", null, "itemId");

    [Fact]
    public async Task StaticStreamOfOwnChannel_WithoutOwnLiveStream_IsDenied()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var r = await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true));
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
        Assert.Equal(403, (await Stream(w, a, "Audio", "GetAudioStream", ("itemId", w.Tuner1Channel.Id), ("static", true))).Status);
        Assert.Equal(403, (await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Program.Id), ("static", true))).Status);
    }

    [Fact]
    public async Task StaticViaLegacyParamsField3_IsDenied()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await Stream(w, a, "Audio", "GetAudioStream", ("itemId", w.Tuner1Channel.Id), ("params", World.Params(3, "true")))).Status);
        Assert.Equal(403, (await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("params", World.Params(3, "TRUE")))).Status);
    }

    [Fact]
    public async Task UniversalAudio_ForALiveTvChannel_IsDenied_ForASong_Allowed()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await Stream(w, a, "UniversalAudio", "GetUniversalAudioStream", ("itemId", w.Tuner1Channel.Id))).Status);
        Assert.Null((await Stream(w, a, "UniversalAudio", "GetUniversalAudioStream", ("itemId", w.Song.Id))).Status);
    }

    [Fact]
    public async Task TrackedPaths_StillAllowed()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);

        // Static with A's own open live stream: serves the open, counted stream.
        Assert.Null((await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), ("liveStreamId", World.Tuner1StreamId))).Status);
        var fields = World.Params(22, World.Tuner1StreamId).Split(';');
        fields[3] = "true";
        Assert.Null((await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("params", string.Join(';', fields)))).Status);

        // A play session id that is really a channel id is not a tracked stream.
        Assert.Equal(403, (await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), ("playSessionId", w.Tuner1Channel.Id.ToString("N")))).Status);

        // Since the final review: a play session alone doesn't make a static request tracked. Jellyfin would stream the
        // job's media source without opening a counted stream (StreamingHelpers.cs:116-125).
        var job = w.AddJob(World.Tuner1StreamId);
        Assert.Equal(403, (await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), ("playSessionId", job))).Status);

        // Not static: the transcoder opens (and counts) the stream.
        Assert.Null((await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", false))).Status);
        Assert.Null((await Stream(w, a, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id))).Status);
        Assert.Null((await Stream(w, a, "DynamicHls", "GetMasterHlsVideoPlaylist", ("itemId", w.Tuner1Channel.Id), ("videoCodec", "copy"))).Status);
    }

    [Fact]
    public async Task LibraryStaticStreams_StillWork_ForEveryone()
    {
        var w = new World();
        foreach (var who in new[] { World.Principal(w.UserA.Id), World.Principal(w.UserC.Id), Anonymous })
        {
            Assert.Null((await Stream(w, who, "Videos", "GetVideoStream", ("itemId", w.Movie.Id), ("static", true))).Status);
            Assert.Null((await Stream(w, who, "Audio", "GetAudioStream", ("itemId", w.Song.Id), ("static", true))).Status);
            Assert.Null((await Stream(w, who, "Audio", "GetAudioStream", ("itemId", w.Song.Id), ("params", World.Params(3, "true")))).Status);
        }
    }

    [Fact]
    public async Task Admins_MayStillStreamStatically()
    {
        var w = new World();
        Assert.Null((await Stream(w, World.Principal(w.Admin.Id), "Videos", "GetVideoStream", ("itemId", w.Tuner2Channel.Id), ("static", true))).Status);
    }
}
