using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 1: users are mapped by id, resolved from usernames when the config is saved.</summary>
public class UserIdMappingTests
{
    private static Dictionary<string, object?> Item(Guid id) => new() { ["itemId"] = id };

    [Fact]
    public void SavingConfig_ResolvesUserNamesToIds()
    {
        var w = new World();
        var users = w.Plugin.Configuration.Users;
        Assert.Equal(w.UserA.Id, users.Single(u => u.TunerKey == "tuner1").UserId);
        Assert.Equal(w.UserB.Id, users.Single(u => u.TunerKey == "tuner2").UserId);
    }

    [Fact]
    public void SavingConfig_WithIdOnly_RefreshesDisplayName()
    {
        var w = new World();
        w.SetConfig(new PluginConfiguration
        {
            Tuners = World.DefaultConfig().Tuners,
            Users = [new UserTunerConfig { UserId = w.UserB.Id, TunerKey = "tuner2" }],
        });
        Assert.Equal("B", w.Plugin.Configuration.Users[0].UserName);
    }

    [Fact]
    public async Task RenamedUser_KeepsTuner()
    {
        var w = new World();
        w.UserA.Username = "A-renamed";
        w.Access.Invalidate();
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
    }

    [Fact]
    public async Task UnknownUserName_IsSkipped_OthersKeepAccess()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [new UserTunerConfig { UserName = "nobody", TunerKey = "tuner1" }, .. config.Users];
        w.SetConfig(config);
        Assert.Null(await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
        Assert.Null(await w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task UnknownTunerKey_IsSkipped_OthersKeepAccess()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [.. config.Users, new UserTunerConfig { UserName = "C", TunerKey = "tuner9" }];
        w.SetConfig(config);
        Assert.Null(await w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserC.Id), Item(w.Tuner1Channel.Id)));
    }

    [Fact]
    public async Task DeletedUserId_IsSkipped_OthersKeepAccess()
    {
        var w = new World();
        w.Users.Remove(w.UserA);
        w.Access.Invalidate();
        Assert.Null(await w.RunFilter(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task NullUsersArray_DeniesMappedUsersButDoesNotThrow()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = null!;
        w.SetConfig(config);
        Assert.Equal(403, await w.RunFilter(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id)));
    }
}
