using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Final review, Sol #2: user work picked up mid channel pass must not be lost when it fails.</summary>
public class ReviewFinalSyncTests
{
    [Fact]
    public async Task FailedInterleavedUserSync_DuringAChannelsOnlyPass_IsRetried()
    {
        var w = new World();
        using var sync = w.CreateSync();
        await sync.StartAsync(CancellationToken.None);
        await World.Eventually(() => sync.IsReady, "initial sync");

        var expected = w.UserA.GetPreference(PreferenceKind.BlockedTags);
        Assert.NotEmpty(expected);

        // A's stored preference drifts (e.g. edited behind the API's back).
        w.UserA.SetPreference(PreferenceKind.BlockedTags, []);

        // During the next channels-only pass, a user change arrives and its save fails once. Jellyfin reloads users
        // from the database on every GetUsers (UserManager.cs:99-104), so a failed save leaves the stored value
        // unchanged: model that by restoring it before throwing.
        var marked = 0;
        var failures = 0;
        w.OnGetItemList = _ =>
        {
            if (Interlocked.Exchange(ref marked, 1) == 0)
            {
                sync.MarkUsersDirty("test: user change mid-pass");
            }
        };
        w.OnUpdateUser = user =>
        {
            if (user.Id == w.UserA.Id && Interlocked.Increment(ref failures) == 1)
            {
                user.SetPreference(PreferenceKind.BlockedTags, []);
                throw new IOException("database is locked");
            }
        };
        sync.MarkChannelsDirty("test: channels-only pass");

        await World.Eventually(() => Volatile.Read(ref failures) >= 1, "the interleaved user save failed");
        await World.Eventually(
            () => w.UserA.GetPreference(PreferenceKind.BlockedTags).SequenceEqual(expected),
            "A's blocked tags restored by a retried users pass");
        await sync.StopAsync(CancellationToken.None);
    }
}

/// <summary>Final review, Argon #4: a failed save of the owned-tag history must not break every request.</summary>
public class ReviewFinalSaveFailureTests
{
    [Fact]
    public async Task SnapshotBuild_SurvivesAFailedConfigSave()
    {
        var w = new World();
        w.Plugin.Configuration.OwnedTagHistory = [];
        w.Access.Invalidate();
        // The config save throws (full disk, read-only mount): BasePluginOfT.SaveConfiguration doesn't catch.
        var saves = 0;
        w.XmlFake.Handlers["SerializeToFile"] = _ =>
        {
            saves++;
            throw new IOException("No space left on device");
        };
        try
        {
            var snapshot = w.Access.Snapshot;
            Assert.Contains("livetv-tuner1", snapshot.OwnedTags);

            var a = World.Principal(w.UserA.Id);
            var args = new System.Collections.Generic.Dictionary<string, object?> { ["itemId"] = w.Tuner1Channel.Id };
            Assert.Null((await w.RunAt(a, args, "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
            args["itemId"] = w.Tuner2Channel.Id;
            Assert.Equal(403, (await w.RunAt(a, args, "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
        }
        finally
        {
            w.XmlFake.Handlers.Remove("SerializeToFile");
        }

        Assert.True(saves >= 1);
    }
}
