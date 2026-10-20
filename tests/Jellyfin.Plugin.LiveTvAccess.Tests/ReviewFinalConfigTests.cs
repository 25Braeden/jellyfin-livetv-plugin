using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review Q5: a name-only user entry that matches no user must not linger in the saved configuration, where a
/// later save would resolve it to whoever renamed themselves to that name (UserController.cs:388-395).
/// </summary>
public class ReviewFinalConfigTests
{
    [Fact]
    public void UnresolvableNameOnlyEntry_IsDroppedAtSave()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [.. config.Users, new UserTunerConfig { UserName = "Dad", TunerKey = "tuner2" }];
        w.Plugin.UpdateConfiguration(config);
        Assert.DoesNotContain(w.Plugin.Configuration.Users, u => string.Equals(u.UserName, "Dad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelfRenameToADanglingName_ThenAdminResave_GrantsNothing()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [.. config.Users, new UserTunerConfig { UserName = "Dad", TunerKey = "tuner2" }];
        w.Plugin.UpdateConfiguration(config);

        w.UserC.Username = "Dad";
        w.Plugin.UpdateConfiguration(w.Plugin.Configuration);
        w.Access.Invalidate();

        Assert.DoesNotContain(w.Plugin.Configuration.Users, u => u.UserId == w.UserC.Id);
        var args = new System.Collections.Generic.Dictionary<string, object?> { ["itemId"] = w.Tuner2Channel.Id };
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserC.Id), args, "Videos", "GetVideoStream", "GET", null, "itemId")).Status);
    }

    [Fact]
    public void NameOnlyEntry_ForAnExistingUser_IsStillResolved()
    {
        var w = new World();
        var config = World.DefaultConfig();
        config.Users = [.. config.Users.Where(u => u.UserId != w.UserB.Id && u.UserName != "B"), new UserTunerConfig { UserName = "B", TunerKey = "tuner2" }];
        w.Plugin.UpdateConfiguration(config);
        Assert.Contains(w.Plugin.Configuration.Users, u => u.UserId == w.UserB.Id && u.TunerKey == "tuner2");
    }
}
