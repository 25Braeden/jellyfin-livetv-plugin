using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Enums;
using LiveTvAccess.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 2: the tag-sync worker lifecycle.</summary>
public class SyncLifecycleTests
{
    private static Dictionary<string, object?> Item(Guid id) => new() { ["itemId"] = id };

    private static string[] Blocked(Jellyfin.Database.Implementations.Entities.User user)
        => user.GetPreference(PreferenceKind.BlockedTags);

    [Fact]
    public async Task UntilFirstSyncCompletes_ProtectedOperationsAreDenied_ThenAllowed()
    {
        var w = new World();
        using var release = new ManualResetEventSlim();
        w.OnGetItemIds = () => release.Wait(TimeSpan.FromSeconds(10));
        using var sync = w.CreateSync();

        await sync.StartAsync(CancellationToken.None);
        Assert.False(sync.IsReady);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));

        release.Set();
        await World.Eventually(() => sync.IsReady, "first sync");
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UntilFirstSyncCompletes_AdminsAndApiKeysStillPass()
    {
        var w = new World();
        using var release = new ManualResetEventSlim();
        w.OnGetItemIds = () => release.Wait(TimeSpan.FromSeconds(10));
        using var sync = w.CreateSync();

        await sync.StartAsync(CancellationToken.None);
        Assert.Null(await w.RunFilter(World.Principal(w.Admin.Id), Item(w.Tuner2Channel.Id)));
        Assert.Null(await w.RunFilter(World.Principal(null, isApiKey: true), Item(w.Tuner2Channel.Id)));
        release.Set();
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FirstSyncFailure_RetriesWithBackoff_StaysClosedUntilSuccess()
    {
        var w = new World();
        var attempts = 0;
        w.OnGetItemIds = () =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw new InvalidOperationException("db not ready");
            }
        };
        using var sync = w.CreateSync();

        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => Volatile.Read(ref attempts) >= 1, "first attempt");
        Assert.False(sync.IsReady);
        await World.Eventually(() => sync.IsReady, "ready after retries");
        Assert.Equal(3, Volatile.Read(ref attempts));
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManySignals_WhileAPassRuns_CoalesceIntoOneMorePass()
    {
        var w = new World();
        using var hold = new ManualResetEventSlim();
        var holdNext = false;
        w.OnGetItemIds = () =>
        {
            if (Volatile.Read(ref holdNext))
            {
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        var before = sync.PassCount;

        Volatile.Write(ref holdNext, true);
        sync.MarkChannelsDirty("test");
        await Task.Delay(100);
        for (var i = 0; i < 100; i++)
        {
            sync.MarkUsersDirty("burst");
            sync.MarkChannelsDirty("burst");
        }

        Volatile.Write(ref holdNext, false);
        hold.Set();
        await World.Eventually(() => sync.PassCount >= before + 2, "two passes");
        await Task.Delay(300);
        Assert.Equal(before + 2, sync.PassCount);
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OwnUserUpdates_DoNotQueueAnotherPass()
    {
        var w = new World();
        using var sync = w.CreateSync();

        // Simulate a Jellyfin version where UpdateUserAsync also raises OnUserUpdated.
        EventHandler<Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User>>? handlers = null;
        w.UsersFake.Handlers["add_OnUserUpdated"] = args =>
        {
            handlers += (EventHandler<Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User>>)args[0]!;
            return null;
        };
        w.OnUpdateUser = u => handlers?.Invoke(w.UserManager, new UserUpdatedEventArgs(u));

        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        Assert.True(w.UserUpdates > 0);
        var passes = sync.PassCount;
        await Task.Delay(300);
        Assert.Equal(passes, sync.PassCount);
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UserCreatedEvent_SyncsTheNewUser()
    {
        var w = new World();
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");

        var dave = w.AddUser("Dave");
        await new UserEventsConsumer(sync, w.Access).OnEvent(new UserCreatedEventArgs(dave));
        await World.Eventually(() => Blocked(dave).Contains(TunerTags.Unassigned), "new user gets blocked tags");
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdminPolicyEdit_ThroughTheApi_TriggersUserSync()
    {
        var w = new World();
        var signals = new ReadySignals();
        w.Signals = signals;
        await w.RunFilter(World.Principal(w.Admin.Id), new Dictionary<string, object?>(), "POST", "Users/{userId}/Policy");
        await w.RunFilter(World.Principal(null, isApiKey: true), new Dictionary<string, object?>(), "POST", "Users/{userId}/Policy");
        Assert.Equal(2, signals.UsersDirty);

        // Reads, logins and non-admin writes don't trigger it.
        await w.RunFilter(World.Principal(w.Admin.Id), new Dictionary<string, object?>(), "GET", "Users/{userId}");
        await w.RunFilter(World.Principal(w.UserA.Id), new Dictionary<string, object?>(), "POST", "Users/Configuration");
        Assert.Equal(2, signals.UsersDirty);
    }

    [Fact]
    public async Task PeriodicPass_RestoresBlockedTagsWithoutAnyEvent()
    {
        var w = new World();
        using var sync = w.CreateSync();
        sync.PeriodicInterval = TimeSpan.FromMilliseconds(100);
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");

        // An admin edit outside the API (no event) clears A's blocked tags.
        w.UserA.SetPreference(PreferenceKind.BlockedTags, Array.Empty<string>());
        await World.Eventually(() => Blocked(w.UserA).Contains("livetv-tuner2"), "periodic pass restores tags");
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Channels_AreProcessedInPages()
    {
        var w = new World();
        for (var i = 0; i < 1200; i++)
        {
            w.AddChannel(World.Tuner1Url, "bulk-" + i);
        }

        var pages = new ConcurrentBag<int>();
        w.OnGetItemList = q => pages.Add(q.ItemIds.Length);
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        await sync.StopAsync(CancellationToken.None);

        Assert.True(pages.Count >= 3, $"expected >= 3 pages, got {pages.Count}");
        Assert.All(pages, size => Assert.InRange(size, 1, 500));
        Assert.All(w.Items.Values.OfType<LiveTvChannel>(), c => Assert.NotEmpty(c.Tags));
    }

    [Fact]
    public async Task UserRestrictions_AreNotHeldUpByALongChannelPass()
    {
        var w = new World();
        for (var i = 0; i < 1200; i++)
        {
            w.AddChannel(World.Tuner1Url, "bulk-" + i);
        }

        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");

        // During the next channel pass, after its first page, a user change arrives.
        var order = new ConcurrentQueue<string>();
        var pageNo = 0;
        w.OnGetItemList = _ =>
        {
            order.Enqueue("page");
            if (Interlocked.Increment(ref pageNo) == 1)
            {
                w.UserB.SetPreference(PreferenceKind.BlockedTags, Array.Empty<string>());
                sync.MarkUsersDirty("test");
            }
        };
        w.OnUpdateUser = _ => order.Enqueue("users");
        sync.MarkChannelsDirty("test");
        await World.Eventually(() => order.Count(o => o == "page") >= 3, "channel pass finished");
        await sync.StopAsync(CancellationToken.None);

        var list = order.ToList();
        Assert.True(list.IndexOf("users") < list.LastIndexOf("page"), "users were synced only after the whole channel pass: " + string.Join(",", list));
    }

    [Fact]
    public async Task FailedChannelSave_IsRetried_AndTheTagIsEventuallyPersisted()
    {
        // Regression: tags were set on the cached item before saving; a failed save left the item looking
        // tagged, so later passes skipped it and the tag was never persisted.
        var w = new World();
        var saves = new ConcurrentBag<Guid>();
        var failures = 0;
        w.LibraryFake.Handlers["UpdateItemAsync"] = args =>
        {
            var item = (BaseItem)args[0]!;
            if (item.Id == w.Tuner1Channel.Id && Interlocked.Increment(ref failures) == 1)
            {
                throw new InvalidOperationException("db busy");
            }

            saves.Add(item.Id);
            return Task.CompletedTask;
        };
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        await sync.StopAsync(CancellationToken.None);

        Assert.True(failures >= 1);
        Assert.Contains(w.Tuner1Channel.Id, saves);
        Assert.Contains("livetv-tuner1", w.Tuner1Channel.Tags);
    }

    [Fact]
    public async Task Stop_CancelsAndAwaitsTheWorker_ThenDisposeIsSafe()
    {
        var w = new World();
        using var release = new ManualResetEventSlim();
        w.OnGetItemIds = () => release.Wait(TimeSpan.FromMilliseconds(500));
        var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await sync.StopAsync(CancellationToken.None);
        sync.Dispose();
        sync.MarkUsersDirty("after dispose");
    }
}
