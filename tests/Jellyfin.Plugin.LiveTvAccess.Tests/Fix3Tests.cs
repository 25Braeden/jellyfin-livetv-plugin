using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 3: snapshots, reserved key, tag ownership, one API-key rule.</summary>
public class Fix3Tests
{
    private const string Tuner3Url = "/tuners/tuner3.m3u";

    private static Dictionary<string, object?> Item(Guid id) => new() { ["itemId"] = id };

    private static string[] Blocked(Jellyfin.Database.Implementations.Entities.User user)
        => user.GetPreference(PreferenceKind.BlockedTags);

    [Fact]
    public void Snapshot_IsStableUntilInvalidated_ThenANewVersion()
    {
        var w = new World();
        var first = w.Access.Snapshot;
        Assert.Same(first, w.Access.Snapshot);
        Assert.Equal(first.Version, w.Access.Version);
        w.Access.Invalidate();
        var second = w.Access.Snapshot;
        Assert.NotSame(first, second);
        Assert.True(second.Version > first.Version);
    }

    [Fact]
    public async Task ReservedTunerKey_SkipsOnlyThatTuner()
    {
        var w = new World();
        w.SetConfig(new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner1Url }, new TunerConfig { Key = "unassigned", Url = World.Tuner2Url }],
            Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner1" }],
        });
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task SyncPass_SupersededByConfigChange_EndsWithTheNewConfigsTags()
    {
        var w = new World();
        for (var i = 0; i < 1200; i++)
        {
            w.AddChannel(World.Tuner1Url, "bulk-" + i);
        }

        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");

        // During the next channel pass, after its first page, the tuner keys are swapped.
        var swapped = new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner2Url }, new TunerConfig { Key = "tuner2", Url = World.Tuner1Url }],
            Users = World.DefaultConfig().Users,
        };
        var page = 0;
        w.OnGetItemList = _ =>
        {
            if (Interlocked.Increment(ref page) == 1)
            {
                w.SetConfig(swapped);
                sync.MarkChannelsDirty("config change");
                sync.MarkUsersDirty("config change");
            }
        };
        sync.MarkChannelsDirty("test");

        await World.Eventually(
            () => w.Items.Values.OfType<MediaBrowser.Controller.LiveTv.LiveTvChannel>()
                .All(c => c.Tags.Contains(c.ExternalId!.StartsWith(JellyfinIds.M3uChannelPrefix(World.Tuner1Url), StringComparison.Ordinal) ? "livetv-tuner2" : "livetv-tuner1")),
            "all channels tagged per the new config",
            10000);
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RemovedTuner_ItsTagIsCleanedUp_FromChannelsAndUsers()
    {
        var w = new World();
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady && w.Tuner2Channel.Tags.Contains("livetv-tuner2"), "first sync");

        w.SetConfig(new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner1Url }],
            Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner1" }],
        });
        sync.MarkChannelsDirty("config change");
        sync.MarkUsersDirty("config change");

        await World.Eventually(
            () => w.Tuner2Channel.Tags.SequenceEqual(["livetv-unassigned"]) && !Blocked(w.UserA).Contains("livetv-tuner2"),
            "tuner2 tag removed everywhere");
        await sync.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ForeignLivetvTags_AreKept_AndReported()
    {
        var w = new World();
        w.UserC.SetPreference(PreferenceKind.BlockedTags, ["livetv-sports"]);
        w.Tuner1Channel.Tags = ["livetv-sports"];
        var log = new ListLogger<TagSyncService>();
        using var sync = w.CreateSync(log);
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        await sync.StopAsync(CancellationToken.None);

        Assert.Contains("livetv-sports", Blocked(w.UserC));
        Assert.Contains("livetv-sports", w.Tuner1Channel.Tags);
        Assert.Contains("livetv-tuner1", w.Tuner1Channel.Tags);
        Assert.Contains(log.Messages, m => m.StartsWith("Warning", StringComparison.Ordinal) && m.Contains("livetv-sports", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewlyClaimedTag_AlreadyInUse_IsReported()
    {
        var w = new World();
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "first sync");
        await sync.StopAsync(CancellationToken.None);

        // An admin had used "livetv-tuner3" for their own purposes before tuner3 is configured.
        w.UserC.SetPreference(PreferenceKind.BlockedTags, ["livetv-tuner3"]);
        var config = World.DefaultConfig();
        config.Tuners = [.. config.Tuners, new TunerConfig { Key = "tuner3", Url = Tuner3Url }];
        w.SetConfig(config);

        var log = new ListLogger<TagSyncService>();
        using var second = w.CreateSync(log);
        await second.StartAsync(CancellationToken.None);
        await World.Eventually(() => second.IsReady, "sync with tuner3");
        await second.StopAsync(CancellationToken.None);

        Assert.Contains(log.Messages, m => m.StartsWith("Warning", StringComparison.Ordinal) && m.Contains("livetv-tuner3", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnedTagHistory_SurvivesAConfigSaveThatOmitsIt()
    {
        var w = new World();
        _ = w.Access.Snapshot;
        Assert.Contains("livetv-tuner2", w.Plugin.Configuration.OwnedTagHistory);

        w.SetConfig(new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner1Url }],
            Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner1" }],
        });
        Assert.Contains("livetv-tuner2", w.Plugin.Configuration.OwnedTagHistory);
        Assert.Contains("livetv-tuner2", w.Access.Snapshot.OwnedTags);
    }

    [Fact]
    public async Task ApiKeyCarryingAUserId_IsNotExempt_InTheFilter()
    {
        var w = new World();
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id, isApiKey: true), Item(w.Tuner2Channel.Id)));
        Assert.Null(await w.RunFilter(World.Principal(null, isApiKey: true), Item(w.Tuner2Channel.Id)));
    }
}
