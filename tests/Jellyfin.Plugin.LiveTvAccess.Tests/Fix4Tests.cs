using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 4: native Live TV permission, unrecognized callers, one generic message.</summary>
public class Fix4Tests
{
    private static Dictionary<string, object?> Item(Guid id) => new() { ["itemId"] = id };

    [Fact]
    public async Task TokenOfADeletedUser_IsDenied()
    {
        var w = new World();
        Assert.Equal(403, await w.RunFilter(World.Principal(Guid.NewGuid()), Item(w.Tuner1Channel.Id)));
    }

    [Fact]
    public async Task AnonymousRequests_NamingLiveTv_AreDenied()
    {
        // Fix 4 let anonymous requests through ("Jellyfin decides"). Fix 5b: Jellyfin doesn't require auth on
        // the stream endpoints, so anonymous callers get default-deny too.
        var w = new World();
        Assert.Equal(403, await w.RunFilter(World.Principal(null, authenticated: false), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task UnrecognizedCaller_NotNamingLiveTv_IsNotTheFiltersBusiness()
    {
        var w = new World();
        Assert.Null(await w.RunFilter(World.PrincipalWithoutUserClaim(), Item(Guid.NewGuid())));
    }

    [Fact]
    public async Task Admin_WithNativeLiveTvOff_StillPasses()
    {
        var w = new World();
        w.Admin.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        Assert.Null(await w.RunFilter(World.Principal(w.Admin.Id), Item(w.Tuner2Channel.Id)));
    }

    [Fact]
    public async Task EveryDenialReason_ReturnsTheSameBody()
    {
        var w = new World();
        var bodies = new List<string?>();

        bodies.Add((await w.RunFilterWithBody(World.Principal(w.UserA.Id), Item(w.Tuner2Channel.Id))).Body);         // other tuner
        bodies.Add((await w.RunFilterWithBody(World.Principal(w.UserC.Id), Item(w.Tuner1Channel.Id))).Body);         // no tuner
        bodies.Add((await w.RunFilterWithBody(World.PrincipalWithoutUserClaim(), Item(w.Tuner1Channel.Id))).Body);  // unrecognized
        var unknown = w.AddChannel("/tuners/unconfigured.m3u", "x");
        bodies.Add((await w.RunFilterWithBody(World.Principal(w.UserA.Id), Item(unknown.Id))).Body);                 // unknown tuner
        w.UserB.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        bodies.Add((await w.RunFilterWithBody(World.Principal(w.UserB.Id), Item(w.Tuner2Channel.Id))).Body);         // native off
        w.Signals = new ReadySignals { IsReady = false };
        bodies.Add((await w.RunFilterWithBody(World.Principal(w.UserA.Id), Item(w.Tuner1Channel.Id))).Body);         // not ready

        Assert.All(bodies, b => Assert.False(string.IsNullOrEmpty(b)));
        Assert.Single(bodies.Distinct());
    }
}
