using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using LiveTvAccess.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Gate 3.5 phase 1: one test per review finding. Each asserts the SECURE behaviour, so a failing
/// test means the finding is reproduced. Reproduced findings carry Category=KnownIssue (excluded from
/// the default run) until phase 2 fixes them. Prefixes: CG = chatgpt-review.txt, GM = gemini-review.txt.
/// </summary>
public class ReviewReproTests
{
    private const string KnownIssue = "KnownIssue";

    private static Dictionary<string, object?> Item(Guid id) => new() { ["itemId"] = id };

    [Fact]
    public async Task Baseline_OwnTunerAllowed_OtherTunerDenied()
    {
        var w = new World();
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task CG1_AuthenticatedPrincipalWithoutUserClaim_IsDenied()
    {
        var w = new World();
        Assert.Equal(403, await w.RunFilter(World.PrincipalWithoutUserClaim(), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    [Trait("Category", KnownIssue)]
    public async Task CG2_UnrecognizedProviderToken_OnOpen_IsDenied()
    {
        var w = new World();
        var token = $"0123456789abcdef0123456789abcdef_LiveTvChannel_{w.Tuner2Channel.Id:N}";
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), new Dictionary<string, object?> { ["openToken"] = token }));
    }

    [Fact]
    [Trait("Category", KnownIssue)]
    public async Task GM2_CG3_TimerBodyOutsideJellyfinApi_OtherTunerChannel_IsDenied()
    {
        var w = new World();
        var body = new TimerInfoDto { ChannelId = w.Tuner2Channel.Id };
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), new Dictionary<string, object?> { ["timerInfo"] = body }));
    }

    [Fact]
    [Trait("Category", KnownIssue)]
    public async Task GM5_ItemIdsArray_OtherTunerChannel_IsDenied()
    {
        var w = new World();
        var args = new Dictionary<string, object?> { ["itemIds"] = new[] { w.Tuner2Channel.Id } };
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), args));
    }

    [Fact]
    public async Task CG7_MappedUserWithNativeLiveTvDisabled_IsDenied()
    {
        var w = new World();
        w.UserA.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
    }

    [Fact]
    public async Task GM8_DuplicateUserEntry_DoesNotWipeOtherUsers()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [.. config.Users, new UserTunerConfig { UserName = "a", TunerKey = "tuner2" }];
        w.SetConfig(config);
        Assert.Null(await w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task CG9_NullUserEntry_DoesNotThrow_AndOthersKeepAccess()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [null!, .. config.Users];
        w.SetConfig(config);
        var status = await Record.ExceptionAsync(() => w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
        Assert.Null(status);
        Assert.Null(await w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task CG6_GM8_UsernameReuse_DoesNotTransferTuner()
    {
        var w = new World();
        w.UserA.Username = "A-renamed";
        var impostor = w.AddUser("A");
        w.Access.Invalidate();
        Assert.Equal(403, await w.RunFilter(World.Principal(impostor.Id), Item(w.Tuner1Channel.Id)));
    }

    [Fact]
    public async Task CG5_ConfigSwapDuringDecision_NeverAllowsForbiddenChannel()
    {
        // Config X: tuner1=/t1, tuner2=/t2, A->tuner1. Config Y: keys swapped (tuner1=/t2, tuner2=/t1), A->tuner2.
        // Under both, A must not reach a /t2 channel. Mixing X's catalog ("tuner2") with Y's map (A->tuner2) allows it.
        var w = new World();
        var x = World.DefaultConfig();
        var y = new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner2Url }, new TunerConfig { Key = "tuner2", Url = World.Tuner1Url }],
            Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner2" }],
        };
        // Control: each config on its own denies.
        w.SetConfig(x);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)));
        w.SetConfig(y);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)));

        // A second thread flips the config continuously while A's request is decided repeatedly.
        using var stop = new CancellationTokenSource();
        var flipper = Task.Run(() =>
        {
            var useY = false;
            while (!stop.IsCancellationRequested)
            {
                useY = !useY;
                w.SetConfig(useY ? y : x);
            }
        });

        var allowed = 0;
        for (var i = 0; i < 20000; i++)
        {
            if (await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)) is null)
            {
                allowed++;
            }
        }

        stop.Cancel();
        await flipper;
        Assert.Equal(0, allowed);
    }

    [Fact]
    public async Task CG16_ApiKeyPrincipalWithUserId_FilterAgreesWithCorePolicy()
    {
        var w = new World();
        var policy = AccessPolicy.Decide(new Caller(w.UserA.Id, true, false, true), w.Access.Snapshot.Map, "tuner2");
        Assert.NotEqual(AccessDecision.Allow, policy);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id, isApiKey: true), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public void GM9_AllDenialsUseOneGenericMessage()
    {
        var messages = new HashSet<string>
        {
            AccessPolicy.Message(AccessDecision.DenyNoTuner),
            AccessPolicy.Message(AccessDecision.DenyOtherTuner),
            AccessPolicy.Message(AccessDecision.DenyUnknownTuner),
        };
        Assert.Single(messages);
    }

    [Fact]
    public void CG14a_UnassignedIsRejectedAsTunerKey()
    {
        Assert.ThrowsAny<ArgumentException>(() => TunerTags.NormalizeKey("unassigned"));
    }

    [Fact]
    public void CG14b_SyncKeepsLivetvTagsThePluginDoesNotOwn()
    {
        // Phase 1 used UserTunerMap.MergeTags, which treated every "livetv-*" tag as the plugin's. Fix 3 replaces it
        // with TagOwnership (owned = configured tuners + "unassigned" + history); same scenario, new API.
        var owned = TagOwnership.Owned(["tuner1", "tuner2"], []);
        var merged = TagOwnership.Merge(["livetv-sports", "kids"], owned, ["livetv-tuner2"], out var foreign);
        Assert.Contains("livetv-sports", merged);
        Assert.Equal(["livetv-sports"], foreign);
    }

    [Fact]
    public async Task CG11_StopAsync_WaitsForRunningSync()
    {
        var w = new World();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = 0;
        w.LibraryFake.Handlers["GetItemList"] = _ =>
        {
            Interlocked.Exchange(ref running, 1);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Exchange(ref running, 0);
            return (IReadOnlyList<BaseItem>)[];
        };
        var (tasks, _) = Fake<ITaskManager>.Create();
        using var sync = new TagSyncService(w.Access, w.LibraryManager, w.UserManager, tasks, NullLogger<TagSyncService>.Instance);

        // StartAsync runs the first sync inline (see N1), so start it on another thread.
        var start = Task.Run(() => sync.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stop = sync.StopAsync(CancellationToken.None);
        var stoppedWhileRunning = stop.IsCompleted && Volatile.Read(ref running) == 1;
        release.Set();
        await stop;
        await start;
        Assert.False(stoppedWhileRunning, "StopAsync returned while a sync was still running");
    }

    [Fact]
    public async Task N1_StartAsync_DoesNotBlockOnInitialSync()
    {
        // New (not in either review): StartAsync runs the startup sync synchronously up to its first await,
        // so a slow channel query (16k channels) runs on Jellyfin's host-start thread.
        var w = new World();
        using var release = new ManualResetEventSlim();
        w.LibraryFake.Handlers["GetItemList"] = _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return (IReadOnlyList<BaseItem>)[];
        };
        var (tasks, _) = Fake<ITaskManager>.Create();
        using var sync = new TagSyncService(w.Access, w.LibraryManager, w.UserManager, tasks, NullLogger<TagSyncService>.Instance);

        var start = Task.Run(() => sync.StartAsync(CancellationToken.None));
        var returnedQuickly = await Task.WhenAny(start, Task.Delay(1000)) == start;
        release.Set();
        await start;
        await sync.StopAsync(CancellationToken.None);
        Assert.True(returnedQuickly, "StartAsync blocked on the initial sync");
    }

    // ---- Findings expected NOT to reproduce (no KnownIssue trait; these should pass) ----

    [Fact]
    public async Task GM1_HyphenatedGuidInOpenToken_IsDenied()
    {
        // Jellyfin parses the token's channel id with new Guid(string), which accepts "D" format.
        // The plugin only parses "N", so it cannot resolve the tuner and fails closed.
        var w = new World();
        var token = $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{w.Tuner2Channel.Id:D}";
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), new Dictionary<string, object?> { ["openToken"] = token }));
    }

    [Fact]
    public async Task GM6_SingleTunerConfig_UnconfiguredTunerChannelIsDenied()
    {
        var w = new World();
        w.SetConfig(new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner1Url }],
            Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner1" }],
        });
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)));
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
    }
}
