using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using LiveTvAccess.Core;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review Q4: with the same stream URL on both tuners, two opens at the same moment could both pass the
/// "already open on another tuner?" check and then share one stream across tuners. Opens of the same channel or
/// media source are serialized in the filter, check and action together.
/// </summary>
public sealed class ReviewFinalOpeningLockTests : IDisposable
{
    private const string SharedUrl = "http://cdn.example/live/shared.ts";

    public ReviewFinalOpeningLockTests() => OpeningLocks.Timeout = TimeSpan.FromSeconds(5);

    public void Dispose() => OpeningLocks.Timeout = TimeSpan.FromSeconds(30);

    private static string SharedStreamId => JellyfinIds.LiveTvStreamId(JellyfinIds.Md5Guid(SharedUrl));

    private static async Task<(int? Status, string? Body)> Run(
        World w, ClaimsPrincipal who, string controller, string action, Guid itemId, Func<Task> next, params (string Name, object? Value)[] extra)
    {
        var args = new Dictionary<string, object?> { ["itemId"] = itemId };
        foreach (var (name, value) in extra)
        {
            args[name] = value;
        }

        var context = World.Context(who, args, "POST", null, controller, action, "itemId");
        var filter = new LiveTvAccessFilter(w.Access, w.Signals, w.Health, NullLogger<LiveTvAccessFilter>.Instance);
        var ran = false;
        await filter.OnActionExecutionAsync(context, async () =>
        {
            ran = true;
            await next();
            return new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller);
        });
        return ran ? (null, null) : ((context.Result as ContentResult)?.StatusCode ?? -1, (context.Result as ContentResult)?.Content);
    }

    private static (LiveTvChannel A, LiveTvChannel B) Twins(World w)
        => (w.AddChannel(World.Tuner1Url, SharedUrl), w.AddChannel(World.Tuner2Url, SharedUrl));

    [Fact]
    public async Task ConcurrentTwinOpens_TheSecondSeesTheFirstsStream_AndIsDenied()
    {
        var w = new World();
        var (twinA, twinB) = Twins(w);
        var bOpening = new TaskCompletionSource();

        // B opens its tuner-2 twin; the stream registers only after a while (like a real open).
        var b = Run(w, World.Principal(w.UserB.Id), "MediaInfo", "GetPostedPlaybackInfo", twinB.Id, async () =>
        {
            bOpening.SetResult();
            await Task.Delay(300);
            w.LiveStreams[SharedStreamId] = "host2";
        });
        await bOpening.Task;

        // A opens its tuner-1 twin meanwhile: it must wait for B's open, then see the stream on tuner 2.
        var a = await Run(w, World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", twinA.Id, () => Task.CompletedTask);
        Assert.Null((await b).Status);
        Assert.Equal(403, a.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, a.Body);
    }

    [Fact]
    public async Task TheLock_IsReleased_WhenTheActionThrows()
    {
        var w = new World();
        var (twinA, _) = Twins(w);
        var a = World.Principal(w.UserA.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(w, a, "MediaInfo", "GetPostedPlaybackInfo", twinA.Id, () => throw new InvalidOperationException("tuner error")));
        Assert.Null((await Run(w, a, "MediaInfo", "GetPostedPlaybackInfo", twinA.Id, () => Task.CompletedTask)).Status);
        Assert.Equal(0, OpeningLocks.ActiveKeys);
    }

    [Fact]
    public async Task TheLock_IsReleased_WhenTheActionIsCancelled()
    {
        var w = new World();
        var (twinA, _) = Twins(w);
        var a = World.Principal(w.UserA.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(w, a, "Videos", "GetVideoStream", twinA.Id, () => throw new OperationCanceledException()));
        Assert.Null((await Run(w, a, "Videos", "GetVideoStream", twinA.Id, () => Task.CompletedTask)).Status);
    }

    [Fact]
    public async Task WaitingLongerThanTheTimeout_IsDenied()
    {
        OpeningLocks.Timeout = TimeSpan.FromMilliseconds(200);
        var w = new World();
        var (twinA, twinB) = Twins(w);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var b = Run(w, World.Principal(w.UserB.Id), "MediaInfo", "GetPostedPlaybackInfo", twinB.Id, async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task;
        var a = await Run(w, World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", twinA.Id, () => Task.CompletedTask);
        Assert.Equal(403, a.Status);
        release.SetResult();
        Assert.Null((await b).Status);
    }

    [Fact]
    public async Task RequestsUsingAnOpenStream_DoNotWait()
    {
        OpeningLocks.Timeout = TimeSpan.FromMilliseconds(200);
        var w = new World();
        var (twinA, _) = Twins(w);
        var a = World.Principal(w.UserA.Id);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var opening = Run(w, a, "MediaInfo", "GetPostedPlaybackInfo", twinA.Id, async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task;

        // HLS / progressive requests that carry the live stream id use the open stream: no lock.
        Assert.Null((await Run(w, a, "DynamicHls", "GetHlsVideoSegment", twinA.Id, () => Task.CompletedTask, ("liveStreamId", World.Tuner1StreamId))).Status);
        Assert.Null((await Run(w, a, "Videos", "GetVideoStream", twinA.Id, () => Task.CompletedTask, ("liveStreamId", World.Tuner1StreamId))).Status);
        release.SetResult();
        await opening;
    }

    [Fact]
    public async Task DifferentChannels_DoNotBlockEachOther()
    {
        OpeningLocks.Timeout = TimeSpan.FromMilliseconds(200);
        var w = new World();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var b = Run(w, World.Principal(w.UserB.Id), "MediaInfo", "GetPostedPlaybackInfo", w.Tuner2Channel.Id, async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task;
        Assert.Null((await Run(w, World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", w.Tuner1Channel.Id, () => Task.CompletedTask)).Status);
        release.SetResult();
        await b;
    }
}
