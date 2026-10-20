using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review Q1: POST /LiveTv/Programs takes the user from the body without checking it against the caller
/// (LiveTvController.cs:656-658), so a non-admin could list another tuner's guide. The filter binds it to the caller.
/// </summary>
public class ReviewFinalUserBindingTests
{
    private static Task<(int? Status, string? Body)> Programs(World w, Guid caller, object body)
        => w.RunAt(World.Principal(caller), new Dictionary<string, object?> { ["body"] = body }, "LiveTv", "GetPrograms", "POST");

    [Fact]
    public async Task MissingUserId_IsSetToTheCaller()
    {
        var w = new World();
        var body = new ProgramsBody();
        Assert.Null((await Programs(w, w.UserA.Id, body)).Status);
        Assert.Equal(w.UserA.Id, body.UserId);

        var empty = new ProgramsBody { UserId = Guid.Empty };
        Assert.Null((await Programs(w, w.UserA.Id, empty)).Status);
        Assert.Equal(w.UserA.Id, empty.UserId);
    }

    [Fact]
    public async Task AnotherUsersId_IsDenied()
    {
        var w = new World();
        var r = await Programs(w, w.UserA.Id, new ProgramsBody { UserId = w.UserB.Id });
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
    }

    [Fact]
    public async Task OwnUserId_IsAllowed()
    {
        var w = new World();
        Assert.Null((await Programs(w, w.UserA.Id, new ProgramsBody { UserId = w.UserA.Id })).Status);
    }

    [Fact]
    public async Task BodyWithoutAUserId_FailsClosed()
    {
        var w = new World();
        Assert.Equal(403, (await Programs(w, w.UserA.Id, new { ChannelIds = Array.Empty<Guid>() })).Status);
    }

    [Fact]
    public async Task QueryUserIdOfAnotherUser_OnLiveTvListings_IsDenied()
    {
        var w = new World();
        var args = new Dictionary<string, object?> { ["userId"] = (Guid?)w.UserB.Id };
        Assert.Equal(403, (await w.RunAt(World.Principal(w.UserA.Id), args, "LiveTv", "GetLiveTvChannels", "GET")).Status);
        args["userId"] = (Guid?)w.UserA.Id;
        Assert.Null((await w.RunAt(World.Principal(w.UserA.Id), args, "LiveTv", "GetLiveTvChannels", "GET")).Status);
    }

    [Fact]
    public async Task Admins_MayQueryForAnyUser()
    {
        var w = new World();
        var body = new ProgramsBody { UserId = w.UserB.Id };
        Assert.Null((await Programs(w, w.Admin.Id, body)).Status);
        Assert.Equal(w.UserB.Id, body.UserId);
    }

    [Fact]
    public void Contract_RequiresTheProgramsBodyUserId()
    {
        Assert.Contains(Health.EndpointContract.Requirements, r => r.Controller == "LiveTv" && r.Action == "GetPrograms" && (r.BodyProperties ?? []).Contains("UserId"));
    }

    public sealed class ProgramsBody
    {
        public Guid? UserId { get; set; }

        public IReadOnlyList<Guid>? ChannelIds { get; set; }
    }
}
