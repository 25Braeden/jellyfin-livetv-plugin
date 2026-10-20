using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review, Sol #1 and Argon #6: only Jellyfin's effective stream parameters (liveStreamId, params field 22)
/// can make a static request tracked, and params field 3 is parsed failing closed.
/// </summary>
public class ReviewFinalStaticTests
{
    private static Task<(int? Status, string? Body)> Stream(World w, string controller, string action, params (string Name, object? Value)[] args)
        => w.RunAt(World.Principal(w.UserA.Id), args.ToDictionary(a => a.Name, a => a.Value), controller, action, "GET", null, "itemId");

    private static string Params(params (int Index, string Value)[] fields)
    {
        var values = Enumerable.Repeat(string.Empty, 29).ToArray();
        foreach (var (index, value) in fields)
        {
            values[index] = value;
        }

        return string.Join(';', values);
    }

    [Theory]
    [InlineData("deviceId")]
    [InlineData("tag")]
    [InlineData("mediaSourceId")]
    [InlineData("subtitleCodec")]
    public async Task UnrelatedArgument_WithOwnOpenStreamId_DoesNotMakeStaticTracked(string argument)
    {
        var w = new World();
        var r = await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), (argument, World.Tuner1StreamId));
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
        Assert.Equal(403, (await Stream(w, "Audio", "GetAudioStream", ("itemId", w.Tuner1Channel.Id), ("static", true), (argument, World.Tuner1StreamId))).Status);
    }

    [Fact]
    public async Task OwnStreamInAnUnrelatedParamsField_DoesNotMakeStaticTracked()
    {
        var w = new World();
        var p = Params((3, "true"), (1, World.Tuner1StreamId));
        Assert.Equal(403, (await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("params", p))).Status);
    }

    [Fact]
    public async Task EffectiveLiveStreamId_StillAllowed()
    {
        var w = new World();
        Assert.Null((await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), ("liveStreamId", World.Tuner1StreamId))).Status);
        Assert.Null((await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("params", Params((3, "true"), (22, World.Tuner1StreamId))))).Status);
    }

    [Fact]
    public async Task ParamsField22_OverridesTheQueryLiveStreamId()
    {
        // Jellyfin uses field 22 when it is set, so a tracked query liveStreamId doesn't vouch for the request.
        var w = new World();
        var p = Params((3, "true"), (22, "not-an-open-stream"));
        Assert.Equal(403, (await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("liveStreamId", World.Tuner1StreamId), ("params", p))).Status);
    }

    [Theory]
    [InlineData(" true ")] // Argon #6 (Jellyfin: not static; fail closed anyway)
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("True")]
    public async Task ParamsField3_AnyNonBlankValueButFalse_IsStatic(string value)
    {
        var w = new World();
        Assert.Equal(403, (await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("params", Params((3, value))))).Status);
    }

    [Fact]
    public async Task ParamsField3False_OverridesStaticTrue_NotStatic()
    {
        var w = new World();
        Assert.Null((await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Tuner1Channel.Id), ("static", true), ("params", Params((3, "false"))))).Status);
    }

    [Fact]
    public async Task LibraryStatic_WithUnrelatedArguments_StillWorks()
    {
        var w = new World();
        Assert.Null((await Stream(w, "Videos", "GetVideoStream", ("itemId", w.Movie.Id), ("static", true), ("deviceId", "some-device"))).Status);
        Assert.Null((await Stream(w, "Audio", "GetAudioStream", ("itemId", w.Song.Id), ("params", Params((3, " true "))))).Status);
    }
}
