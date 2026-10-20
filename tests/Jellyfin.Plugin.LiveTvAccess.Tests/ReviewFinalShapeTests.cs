using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using Jellyfin.Plugin.LiveTvAccess.Health;
using LiveTvAccess.Core;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review, Sol #3: the self-check must only accept response shapes the scrubber handles, and the scrubber
/// must refuse unexpected shapes on the actions whose responses can carry Live TV sources or configuration.
/// </summary>
public class ReviewFinalShapeTests
{
    private const string Upstream = "http://alice:secret@provider.example/live/1.ts?password=secret";

    private static MediaSourceInfo LiveSource() => new()
    {
        Path = Upstream,
        OpenToken = JellyfinIds.LiveTvProviderPrefix + "_LiveTvChannel_x",
        RequiredHttpHeaders = new Dictionary<string, string> { ["Authorization"] = "Basic c2VjcmV0" },
    };

    [Fact]
    public void Scrubber_DeclaresTheShapesItHandles()
    {
        Assert.True(ResponseScrubber.Supports(typeof(PlaybackInfoResponse)));
        Assert.True(ResponseScrubber.Supports(typeof(LiveStreamResponse)));
        Assert.True(ResponseScrubber.Supports(typeof(BaseItemDto)));
        Assert.True(ResponseScrubber.Supports(typeof(MediaBrowser.Model.Querying.QueryResult<BaseItemDto>)));
        Assert.True(ResponseScrubber.Supports(typeof(IReadOnlyList<SessionInfoDto>)));
        Assert.True(ResponseScrubber.Supports(typeof(BaseItemDto[])));
        Assert.True(ResponseScrubber.Supports(typeof(LiveTvOptions)));
        Assert.False(ResponseScrubber.Supports(typeof(Envelope<PlaybackInfoResponse>)));
        Assert.False(ResponseScrubber.Supports(typeof(Dictionary<string, object>)));
        Assert.False(ResponseScrubber.Supports(typeof(object)));
    }

    [Fact]
    public void Contract_RejectsAWrapperTheScrubberIgnores()
    {
        var shapes = EndpointContract.Requirements.Select(r => new ActionShape(
            r.Controller,
            r.Action,
            r.Parameters.Select(p => new ParameterShape(p, typeof(string)))
                .Concat(r.BodyProperties is null ? [] : [new ParameterShape("body", typeof(Fix7Tests.FakeBody))])
                .ToList(),
            r.Returns is null ? typeof(Task<ActionResult>) : typeof(Task<>).MakeGenericType(typeof(ActionResult<>).MakeGenericType(r.Returns)),
            (r.TemplatePrefix ?? string.Empty) + "x")).ToList();
        Assert.Empty(EndpointContract.FindMissing(shapes));

        var info = shapes.Single(s => s.Controller == "MediaInfo" && s.Action == "GetPostedPlaybackInfo");
        shapes[shapes.IndexOf(info)] = info with { ReturnType = typeof(Task<ActionResult<Envelope<PlaybackInfoResponse>>>) };
        var missing = EndpointContract.FindMissing(shapes);
        Assert.Contains(missing, m => m.Contains("GetPostedPlaybackInfo", StringComparison.Ordinal) && m.Contains("scrubber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedSuccessShape_OnASensitiveAction_IsRefused()
    {
        var w = new World();
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", new ObjectResult(new Envelope<PlaybackInfoResponse>(new PlaybackInfoResponse { MediaSources = [LiveSource()] })));
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(403, content.StatusCode);
        Assert.DoesNotContain("secret", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonObjectSuccessResult_OnASensitiveAction_IsRefused()
    {
        var w = new World();
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", new JsonResult(new { Path = Upstream }));
        Assert.Equal(403, Assert.IsType<ContentResult>(result).StatusCode);
    }

    [Fact]
    public async Task ErrorResponses_OnSensitiveActions_PassUnchanged()
    {
        var w = new World();
        var notFound = new NotFoundObjectResult(new ProblemDetails { Title = "Not found" });
        Assert.Same(notFound, await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", notFound));
        var noContent = new NoContentResult();
        Assert.Same(noContent, await w.RunWithResult(World.Principal(w.UserA.Id), "Session", "GetSessions", noContent));
    }

    [Fact]
    public async Task LiveTvConfig_MustBeLiveTvOptions_OtherKeysUnaffected()
    {
        var w = new World();
        var a = World.Principal(w.UserA.Id);
        var odd = await w.RunWithResult(a, "Configuration", "GetNamedConfiguration", new ObjectResult(new Dictionary<string, object> { ["TunerHosts"] = Upstream }), new Dictionary<string, object?> { ["key"] = "livetv" });
        Assert.Equal(403, Assert.IsType<ContentResult>(odd).StatusCode);

        var encoding = new ObjectResult(new EncodingOptions());
        Assert.Same(encoding, await w.RunWithResult(a, "Configuration", "GetNamedConfiguration", encoding, new Dictionary<string, object?> { ["key"] = "encoding" }));
    }

    [Fact]
    public async Task SupportedShapes_StillScrubbed_AndAdminsUnaffected()
    {
        var w = new World();
        var response = new ObjectResult(new PlaybackInfoResponse { MediaSources = [LiveSource()] });
        var scrubbed = Assert.IsType<ObjectResult>(await w.RunWithResult(World.Principal(w.UserA.Id), "MediaInfo", "GetPostedPlaybackInfo", response));
        Assert.Null(((PlaybackInfoResponse)scrubbed.Value!).MediaSources[0].Path);

        var odd = new ObjectResult(new Envelope<PlaybackInfoResponse>(new PlaybackInfoResponse()));
        Assert.Same(odd, await w.RunWithResult(World.Principal(w.Admin.Id), "MediaInfo", "GetPostedPlaybackInfo", odd));
    }

    public sealed class Envelope<T>
    {
        public Envelope(T value) => Value = value;

        public T Value { get; }
    }
}
