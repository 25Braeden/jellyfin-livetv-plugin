using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Api;
using Jellyfin.Plugin.LiveTvAccess.Health;
using LiveTvAccess.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Phase 2 fix 7: startup self-check, failed state, status API.</summary>
public class Fix7Tests
{
    private static ClaimsPrincipal A(World w) => World.Principal(w.UserA.Id);

    /// <summary>Action shapes that meet every requirement.</summary>
    private static List<ActionShape> GoodShapes()
        => EndpointContract.Requirements.Select(r => new ActionShape(
                r.Controller,
                r.Action,
                r.Parameters.Select(p => new ParameterShape(p, typeof(string)))
                    .Concat(r.BodyProperties is null ? [] : [new ParameterShape("body", typeof(FakeBody))])
                    .ToList(),
                r.Returns is null ? typeof(Task<ActionResult>) : typeof(Task<>).MakeGenericType(typeof(ActionResult<>).MakeGenericType(r.Returns)),
                (r.TemplatePrefix ?? string.Empty) + "x"))
            .ToList();

    private static (SelfCheckService Service, FakeSurface Surface, List<ChannelInfo> TunerChannels, ListLogger<SelfCheckService> Log) Create(World w)
    {
        var surface = new FakeSurface { Actions = GoodShapes() };
        var tunerChannels = new List<ChannelInfo>();
        var (tuner, tunerFake) = Fake<ITunerHost>.Create();
        tunerFake.Handlers["get_Type"] = _ => "m3u";
        tunerFake.Handlers["GetChannels"] = _ => Task.FromResult(tunerChannels);
        var (hosts, hostsFake) = Fake<ITunerHostManager>.Create();
        hostsFake.Handlers["get_TunerHosts"] = _ => (IReadOnlyList<ITunerHost>)[tuner];
        var (tasks, _) = Fake<ITaskManager>.Create();
        var log = new ListLogger<SelfCheckService>();
        var service = new SelfCheckService(w.Access, w.Signals, w.LibraryManager, w.ConfigurationManager, surface, hosts, tasks, log);
        return (service, surface, tunerChannels, log);
    }

    private static string Json(SelfCheckStatus status) => JsonSerializer.Serialize(status);

    // ----- endpoint contract -----

    [Fact]
    public void Contract_CoversWhatTheFilterReliesOn()
    {
        bool Has(string c, string a, params string[] ps)
            => EndpointContract.Requirements.Any(r => r.Controller == c && r.Action == a && ps.All(r.Parameters.Contains));

        Assert.True(Has("Videos", "GetVideoStream", "itemId", "static", "params", "liveStreamId", "playSessionId"));
        Assert.True(Has("Audio", "GetAudioStream", "itemId", "static", "params", "liveStreamId", "playSessionId"));
        Assert.True(Has("MediaInfo", "OpenLiveStream", "openToken"));
        Assert.True(Has("MediaInfo", "CloseLiveStream", "liveStreamId"));
        Assert.True(Has("LiveTv", "GetLiveStreamFile", "streamId"));
        Assert.True(Has("HlsSegment", "GetHlsVideoSegmentLegacy", "itemId", "playlistId", "segmentId", "segmentContainer"));
        Assert.True(Has("Configuration", "GetNamedConfiguration", "key"));
        Assert.True(Has("UniversalAudio", "GetUniversalAudioStream", "itemId"));
        Assert.All(Filters.LiveTvEndpoints.Allowlist, a => Assert.True(Has("LiveTv", a)));
    }

    [Fact]
    public void Contract_GoodShapes_NothingMissing()
    {
        Assert.Empty(EndpointContract.FindMissing(GoodShapes()));
        Assert.Empty(EndpointContract.FindMissingModelMembers());
    }

    [Fact]
    public void Contract_DetectsDrift()
    {
        var shapes = GoodShapes();
        var video = shapes.Single(s => s.Controller == "Videos" && s.Action == "GetVideoStream");
        shapes[shapes.IndexOf(video)] = video with { Parameters = video.Parameters.Where(p => p.Name != "static").ToList() };
        shapes.RemoveAll(s => s.Controller == "MediaInfo" && s.Action == "CloseLiveStream");
        var info = shapes.Single(s => s.Controller == "MediaInfo" && s.Action == "GetPostedPlaybackInfo");
        shapes[shapes.IndexOf(info)] = info with { ReturnType = typeof(Task<ActionResult>) };
        var open = shapes.Single(s => s.Controller == "MediaInfo" && s.Action == "OpenLiveStream");
        shapes[shapes.IndexOf(open)] = open with { Parameters = open.Parameters.Select(p => p.Name == "body" ? p with { Type = typeof(string) } : p).ToList() };
        var policy = shapes.Single(s => s.Controller == "User" && s.Action == "UpdateUserPolicy");
        shapes[shapes.IndexOf(policy)] = policy with { Template = "Admin/x" };

        var missing = EndpointContract.FindMissing(shapes);
        Assert.Contains(missing, m => m.Contains("Videos.GetVideoStream", StringComparison.Ordinal) && m.Contains("static", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.Contains("MediaInfo.CloseLiveStream", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.Contains("GetPostedPlaybackInfo", StringComparison.Ordinal) && m.Contains("PlaybackInfoResponse", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.Contains("OpenLiveStream", StringComparison.Ordinal) && m.Contains("OpenToken", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.Contains("UpdateUserPolicy", StringComparison.Ordinal) && m.Contains("Users/", StringComparison.Ordinal));
    }

    // ----- self-check service -----

    [Fact]
    public void BeforeTheFirstRun_IsPending_AndNotHealthy()
    {
        var (service, _, _, _) = Create(new World());
        Assert.False(service.IsHealthy);
        Assert.Equal(SelfCheckStatus.Pending, service.Status.State);
    }

    [Fact]
    public async Task EverythingMatches_IsHealthy_WithCountsAndNoUrls()
    {
        var w = new World();
        var (service, _, _, _) = Create(w);
        var status = await service.RunAsync("test", CancellationToken.None);

        Assert.Equal(SelfCheckStatus.Healthy, status.State);
        Assert.True(service.IsHealthy);
        Assert.Empty(status.Problems);
        Assert.All(status.Checks, c => Assert.True(c.Ok, c.Name + ": " + c.Detail));
        Assert.Contains(status.Checks, c => c.Name.Contains("filter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(status.Checks, c => c.Name.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(status.Checks, c => c.Name.Contains("claim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(status.Checks, c => c.Name.Contains("tuner", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["tuner1", "tuner2"], status.Tuners.Select(t => t.Key));
        Assert.All(status.Tuners, t => Assert.True(t.ChannelCount >= 1 && t.InJellyfin));
        Assert.Equal("file", status.Tuners[0].Location);
        Assert.True(status.InitialSyncComplete);
        Assert.DoesNotContain("/tuners/", Json(status), StringComparison.Ordinal);
        Assert.DoesNotContain("tuner1.m3u", Json(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterNotRegistered_Fails()
    {
        var (service, surface, _, log) = Create(new World());
        surface.FilterRegistered = false;
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.False(service.IsHealthy);
        Assert.Contains(status.Problems, p => p.Contains("filter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log.Messages, m => m.Contains("SELF-CHECK FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndpointShapesChanged_Fails()
    {
        var (service, surface, _, _) = Create(new World());
        surface.Actions = surface.Actions.Where(a => a.Action != "GetVideoStream").ToList();
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("Videos.GetVideoStream", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Jellyfin-UserId", "Jellyfin-ApiKey")]
    [InlineData("jellyfin-user", "Jellyfin-IsApiKey")]
    public async Task ClaimContractBroken_Fails(string? userId, string? isApiKey)
    {
        var (service, surface, _, _) = Create(new World());
        surface.Claims = userId is null ? null : (userId, isApiKey!);
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("claim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnlyUnrecognizedPrincipalsSeen_Fails_ThenRecovers()
    {
        var w = new World();
        var (service, _, _, _) = Create(w);
        for (var i = 0; i < 3; i++)
        {
            w.Access.GetCaller(World.PrincipalWithoutUserClaim());
        }

        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("claim", StringComparison.OrdinalIgnoreCase));

        w.Access.GetCaller(A(w));
        Assert.True((await service.RunAsync("test", CancellationToken.None)).State == SelfCheckStatus.Healthy);
    }

    [Fact]
    public async Task TunerNotMatchingJellyfin_Fails_AndRecoversWhenFixed()
    {
        var w = new World();
        var (service, _, _, log) = Create(w);
        var host2 = w.LiveTv.TunerHosts[1];
        w.LiveTv.TunerHosts = [w.LiveTv.TunerHosts[0]];
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("tuner2", StringComparison.Ordinal));
        Assert.False(status.Tuners.Single(t => t.Key == "tuner2").InJellyfin);

        w.LiveTv.TunerHosts = [w.LiveTv.TunerHosts[0], host2];
        status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Healthy, status.State);
        Assert.Contains(log.Messages, m => m.Contains("restored", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log.Messages, m => m.Contains("/tuners/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTunersConfigured_Fails()
    {
        var w = new World();
        w.Plugin.Configuration.Tuners = [];
        w.Access.Invalidate();
        var (service, _, _, _) = Create(w);
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("No tuners", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateStreamUrls_WarnOnly_RedactedHostAndHash()
    {
        var w = new World();
        var twin = w.AddChannel(World.Tuner2Url, "stream-a");
        var (service, _, tunerChannels, log) = Create(w);
        tunerChannels.Add(new ChannelInfo { Id = twin.ExternalId, TunerHostId = "host2", Path = "http://alice:pw@cdn.example:81/live/a.ts?token=abc" });

        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Healthy, status.State);
        Assert.True(service.IsHealthy);
        var duplicate = Assert.Single(status.Duplicates);
        Assert.Equal("http://cdn.example:81", duplicate.Host);
        Assert.StartsWith("sha256:", duplicate.Hash, StringComparison.Ordinal);
        Assert.Equal(["tuner1", "tuner2"], duplicate.Tuners);
        Assert.Contains(status.Warnings, m => m.Contains("same stream URL", StringComparison.Ordinal));
        foreach (var secret in new[] { "alice", "pw@", "token", "/live/", "a.ts" })
        {
            Assert.DoesNotContain(secret, Json(status), StringComparison.Ordinal);
            Assert.DoesNotContain(log.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        }

        Assert.Contains(log.Messages, m => m.Contains("cdn.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckThatThrows_IsAFailedState_NotAnException()
    {
        var w = new World();
        var (service, _, _, _) = Create(w);
        w.LibraryFake.Handlers["GetItemIds"] = _ => throw new InvalidOperationException("database is gone");
        var status = await service.RunAsync("test", CancellationToken.None);
        Assert.Equal(SelfCheckStatus.Failed, status.State);
        Assert.Contains(status.Problems, p => p.Contains("could not run", StringComparison.OrdinalIgnoreCase));
    }

    // ----- failed state in the filter -----

    [Fact]
    public async Task FailedState_DeniesLiveTvForNonAdmins_AdminsAndLibraryKeepWorking_RecoversWhenHealthy()
    {
        var w = new World { Health = new StaticSelfCheck(false) };
        var a = A(w);
        Task<(int? Status, string? Body)> Stream(System.Security.Claims.ClaimsPrincipal who, Guid item)
            => w.RunAt(who, new Dictionary<string, object?> { ["itemId"] = item }, "Videos", "GetVideoStream", "GET", null, "itemId");

        var denied = await Stream(a, w.Tuner1Channel.Id);
        Assert.Equal(403, denied.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, denied.Body);
        Assert.Equal(403, (await w.RunAt(a, new Dictionary<string, object?>(), "LiveTv", "GetLiveTvChannels", "GET")).Status);
        Assert.Null((await Stream(a, w.Movie.Id)).Status);
        Assert.Null((await Stream(World.Principal(w.Admin.Id), w.Tuner2Channel.Id)).Status);

        ((StaticSelfCheck)w.Health).IsHealthy = true;
        Assert.Null((await Stream(a, w.Tuner1Channel.Id)).Status);
    }

    // ----- status API -----

    [Fact]
    public void StatusApi_IsAdminOnly()
    {
        var type = typeof(LiveTvAccessController);
        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal("LiveTvAccess", type.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(type.GetMethod(nameof(LiveTvAccessController.GetStatus))?.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task StatusApi_ReturnsTheLastResult_OrRunsOnRefresh()
    {
        var w = new World();
        var (service, _, _, _) = Create(w);
        var controller = new LiveTvAccessController(service);
        Assert.Equal(SelfCheckStatus.Pending, (await controller.GetStatus(false, CancellationToken.None)).Value!.State);
        Assert.Equal(SelfCheckStatus.Healthy, (await controller.GetStatus(true, CancellationToken.None)).Value!.State);
        Assert.Equal(SelfCheckStatus.Healthy, (await controller.GetStatus(false, CancellationToken.None)).Value!.State);
    }

    public sealed class FakeBody
    {
        public string? OpenToken { get; set; }

        public string? LiveStreamId { get; set; }

        public string? MediaSourceId { get; set; }
    }

    private sealed class FakeSurface : IJellyfinSurface
    {
        public bool FilterRegistered { get; set; } = true;

        public List<ActionShape> Actions { get; set; } = [];

        public (string UserId, string IsApiKey)? Claims { get; set; } = ("Jellyfin-UserId", "Jellyfin-IsApiKey");

        public bool IsFilterRegistered() => FilterRegistered;

        public IReadOnlyList<ActionShape> GetActions() => Actions;

        public (string UserId, string IsApiKey)? GetClaimTypes() => Claims;
    }
}
