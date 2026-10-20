using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 5c: legacy HLS file routes bound to a running job (option A1).</summary>
public class Fix5cTests
{
    private const string Controller = "HlsSegment";
    private const string VideoSegment = "GetHlsVideoSegmentLegacy";
    private const string AudioSegment = "GetHlsAudioSegmentLegacy";
    private const string Playlist = "GetHlsPlaylistLegacy";

    private static System.Security.Claims.ClaimsPrincipal Anonymous => World.Principal(null, authenticated: false);

    private static Task<(int? Status, string? Body)> Segment(World w, System.Security.Claims.ClaimsPrincipal who, object itemId, string playlistId, string segmentId, string container = "ts")
        => w.RunAt(
            who,
            new Dictionary<string, object?> { ["itemId"] = itemId.ToString(), ["playlistId"] = playlistId, ["segmentId"] = segmentId, ["segmentContainer"] = container },
            Controller,
            VideoSegment,
            "GET",
            null,
            "itemId",
            "playlistId",
            "segmentId",
            "segmentContainer");

    private static Task<(int? Status, string? Body)> Audio(World w, System.Security.Claims.ClaimsPrincipal who, object itemId, string segmentId)
        => w.RunAt(who, new Dictionary<string, object?> { ["itemId"] = itemId.ToString(), ["segmentId"] = segmentId }, Controller, AudioSegment, "GET", null, "itemId", "segmentId");

    [Fact]
    public async Task LiveJobOnOwnTuner_LoggedIn_Allowed_OtherTuner_Denied()
    {
        var w = new World();
        var own = w.AddHlsJob("aaaa1111aaaa1111aaaa1111aaaa1111", World.Tuner1StreamId);
        var other = w.AddHlsJob("bbbb2222bbbb2222bbbb2222bbbb2222", World.Tuner2StreamId);
        var a = World.Principal(w.UserA.Id);
        Assert.Null((await Segment(w, a, w.Tuner1Channel.Id, own, own + "0")).Status);
        Assert.Null((await Segment(w, a, w.Tuner1Channel.Id, own, own + "-1", "mp4")).Status);
        Assert.Equal(403, (await Segment(w, a, w.Tuner2Channel.Id, other, other + "0")).Status);
        Assert.Equal(403, (await Segment(w, a, w.Tuner1Channel.Id, other, other + "0")).Status);
    }

    [Fact]
    public async Task LiveJob_Anonymous_IsAllowed_A1()
    {
        var w = new World();
        var job = w.AddHlsJob("cccc3333cccc3333cccc3333cccc3333", World.Tuner2StreamId);
        Assert.Null((await Segment(w, Anonymous, w.Tuner2Channel.Id, job, job + "7")).Status);
    }

    [Fact]
    public async Task LibraryJob_IsAllowed_ForEveryone()
    {
        var w = new World();
        var job = w.AddHlsJob("dddd4444dddd4444dddd4444dddd4444", null);
        Assert.Null((await Segment(w, Anonymous, w.Movie.Id, job, job + "0")).Status);
        Assert.Null((await Segment(w, World.Principal(w.UserC.Id), w.Movie.Id, job, job + "0")).Status);
    }

    [Fact]
    public async Task NoRunningJob_FinishedOrNonExistent_IsDenied()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        Assert.Equal(403, (await Segment(w, a, w.Movie.Id, "eeee5555eeee5555eeee5555eeee5555", "eeee5555eeee5555eeee5555eeee55550")).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, "eeee5555eeee5555eeee5555eeee5555", "eeee5555eeee5555eeee5555eeee55550")).Status);
        var finished = w.AddHlsJob("ffff6666ffff6666ffff6666ffff6666", null);
        w.Jobs.Clear();
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, finished, finished + "0")).Status);
    }

    [Fact]
    public async Task RawLiveBufferFilename_IsDenied()
    {
        // The shared .ts buffer is written to the transcode folder as {UniqueId}.ts (LiveStream.cs:79): not a job.
        var w = new World();
        var unique = Guid.NewGuid().ToString("N");
        Assert.Equal(403, (await Segment(w, Anonymous, Guid.Empty.ToString("N"), unique, unique)).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, unique, unique)).Status);
        Assert.Equal(403, (await Audio(w, Anonymous, w.Movie.Id, unique)).Status);
    }

    [Theory]
    [InlineData("..", "x0")]
    [InlineData("../x", "x0")]
    [InlineData("job", "../../../etc/passwd")]
    [InlineData("job", "job/../job0")]
    [InlineData("a/b", "a/b0")]
    [InlineData("a\\b", "a\\b0")]
    [InlineData("job", "job0.ts")]
    [InlineData("%2e%2e", "%2e%2e0")]
    [InlineData("job", "")]
    public async Task TraversalAndMalformedNames_AreDenied(string playlistId, string segmentId)
    {
        var w = new World();
        w.AddHlsJob("job", null);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, playlistId, segmentId)).Status);
        Assert.Equal(403, (await Segment(w, World.Principal(w.UserA.Id), w.Movie.Id, playlistId, segmentId)).Status);
    }

    [Theory]
    [InlineData("m3u8")]
    [InlineData("log")]
    [InlineData("txt")]
    public async Task NonSegmentExtensions_AreDenied(string container)
    {
        var w = new World();
        var job = w.AddHlsJob("job", null);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, job, job + "0", container)).Status);
    }

    [Fact]
    public async Task SegmentNotBelongingToTheJob_IsDenied()
    {
        var w = new World();
        var job = w.AddHlsJob("abcdabcdabcdabcdabcdabcdabcdabcd", null);
        w.AddHlsJob("other", null);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, job, "other0")).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, job, job)).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, w.Movie.Id, job, job + "x")).Status);
    }

    [Fact]
    public async Task EmptyOrUnresolvableItemId_IsDenied()
    {
        var w = new World();
        var job = w.AddHlsJob("job", null);
        Assert.Equal(403, (await Segment(w, Anonymous, Guid.Empty.ToString("N"), job, job + "0")).Status);
        Assert.Equal(403, (await Segment(w, World.Principal(w.UserA.Id), Guid.Empty.ToString("N"), job, job + "0")).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, Guid.NewGuid().ToString("N"), job, job + "0")).Status);
        Assert.Equal(403, (await Segment(w, Anonymous, "not-a-guid", job, job + "0")).Status);
    }

    [Fact]
    public async Task AudioLegacyRoute_ExactJobOnly()
    {
        var w = new World();
        var job = w.AddHlsJob("f182b233fa90302addc4c6e75df854e8", null);
        Assert.Null((await Audio(w, Anonymous, w.Song.Id, job + "0")).Status);
        Assert.Equal(403, (await Audio(w, Anonymous, w.Song.Id, "f182b233fa90302addc4c6e75df854e")).Status);
        Assert.Equal(403, (await Audio(w, Anonymous, w.Song.Id, "../" + job + "0")).Status);
        var live = w.AddHlsJob("0123abcd0123abcd0123abcd0123abcd", World.Tuner2StreamId);
        Assert.Equal(403, (await Audio(w, World.Principal(w.UserA.Id), w.Song.Id, live + "3")).Status);
    }

    [Fact]
    public async Task LegacyPlaylistRoute_ExactJobOnly()
    {
        var w = new World();
        var job = w.AddHlsJob("job", null);
        var a = World.Principal(w.UserA.Id);
        Assert.Null((await w.RunAt(a, new Dictionary<string, object?> { ["itemId"] = w.Movie.Id.ToString("N"), ["playlistId"] = job }, Controller, Playlist, "GET", null, "itemId", "playlistId")).Status);
        Assert.Equal(403, (await w.RunAt(a, new Dictionary<string, object?> { ["itemId"] = w.Movie.Id.ToString("N"), ["playlistId"] = "nojob" }, Controller, Playlist, "GET", null, "itemId", "playlistId")).Status);
    }

    [Fact]
    public async Task Admins_AreUnaffected_AndDenialsUseTheGenericMessage()
    {
        var w = new World();
        Assert.Null((await Segment(w, World.Principal(w.Admin.Id), Guid.Empty.ToString("N"), "nojob", "nojob0")).Status);
        Assert.Equal(AccessPolicy.DeniedMessage, (await Segment(w, Anonymous, Guid.Empty.ToString("N"), "nojob", "nojob0")).Body);
    }

    [Fact]
    public async Task StopEncodingProcess_IsStillCheckedByTheGenericRules()
    {
        var w = new World();
        var other = w.AddJob(World.Tuner2StreamId);
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserA.Id), new Dictionary<string, object?> { ["deviceId"] = "d", ["playSessionId"] = other }, Controller, "StopEncodingProcess", "DELETE")).Status);
    }
}
