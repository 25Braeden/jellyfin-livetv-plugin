using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 6: the live buffer URLs are for the server itself (Jellyfin's ffmpeg) only.</summary>
public class Fix6Tests
{
    private const string StreamFile = "GetLiveStreamFile";
    private const string RecordingFile = "GetLiveRecordingFile";

    private static ClaimsPrincipal Anonymous => World.Principal(null, authenticated: false);

    private static async Task<(int? Status, string? Body)> Buffer(
        World w, ClaimsPrincipal who, string action, string remote, string? local = null, params (string Name, string Value)[] headers)
    {
        var args = new Dictionary<string, object?>
        {
            [action == StreamFile ? "streamId" : "recordingId"] = Guid.NewGuid().ToString("N"),
            ["container"] = "ts",
        };
        var context = World.Context(who, args, "GET", null, "LiveTv", action);
        context.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.HttpContext.Connection.LocalIpAddress = local is null ? null : IPAddress.Parse(local);
        foreach (var (name, value) in headers)
        {
            context.HttpContext.Request.Headers[name] = value;
        }

        var filter = new Filters.LiveTvAccessFilter(w.Access, w.Signals, NullLogger<Filters.LiveTvAccessFilter>.Instance);
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
        });
        return ran ? (null, null) : ((context.Result as ContentResult)?.StatusCode ?? -1, (context.Result as ContentResult)?.Content);
    }

    [Theory]
    [InlineData(StreamFile, "127.0.0.1", null)]
    [InlineData(StreamFile, "::1", null)]
    [InlineData(StreamFile, "172.18.0.5", "172.18.0.5")]
    [InlineData(RecordingFile, "127.0.0.1", null)]
    public async Task ServerItself_NoForwardedHeaders_IsAllowed(string action, string remote, string? local)
    {
        Assert.Null((await Buffer(new World(), Anonymous, action, remote, local)).Status);
    }

    [Theory]
    [InlineData(StreamFile, "172.18.0.9", "172.18.0.5")]
    [InlineData(StreamFile, "192.168.65.1", "172.18.0.5")]
    [InlineData(RecordingFile, "203.0.113.7", "172.18.0.5")]
    public async Task OtherAddresses_AreDenied(string action, string remote, string local)
    {
        var r = await Buffer(new World(), Anonymous, action, remote, local);
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("X-Real-IP", "203.0.113.7")]
    [InlineData("Forwarded", "for=203.0.113.7")]
    [InlineData("X-Forwarded-Host", "tv.example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Original-For", "172.18.0.2:41234")]
    [InlineData("X-Original-Proto", "http")]
    [InlineData("X-Original-Host", "jellyfin:8096")]
    public async Task ViaAProxy_FromLoopback_IsDenied(string header, string value)
    {
        Assert.Equal(403, (await Buffer(new World(), Anonymous, StreamFile, "127.0.0.1", null, (header, value))).Status);
        Assert.Equal(403, (await Buffer(new World(), Anonymous, RecordingFile, "127.0.0.1", null, (header, value))).Status);
    }

    [Fact]
    public async Task LoggedInUser_FromOutside_IsDenied()
    {
        var w = new World();
        Assert.Equal(403, (await Buffer(w, World.Principal(w.UserA.Id), StreamFile, "192.168.65.1", "172.18.0.5")).Status);
    }

    [Fact]
    public async Task Admins_KeepAccess_EvenViaAProxy()
    {
        var w = new World();
        Assert.Null((await Buffer(w, World.Principal(w.Admin.Id), StreamFile, "203.0.113.7", "172.18.0.5", ("X-Forwarded-For", "203.0.113.7"))).Status);
    }

    [Fact]
    public async Task Images_StillPassThroughAnonymously()
    {
        var w = new World();
        var r = await w.RunAt(Anonymous, new Dictionary<string, object?> { ["itemId"] = w.Tuner2Channel.Id }, "Image", "GetItemImage", "GET", null, "itemId");
        Assert.Null(r.Status);
    }
}
