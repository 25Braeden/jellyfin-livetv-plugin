using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Global MVC action filter that enforces per-user tuner access before Jellyfin does any work. Default-deny for
/// authenticated non-admin, non-API-key callers:
/// <list type="bullet">
/// <item>LiveTvController: only <see cref="LiveTvEndpoints.Allowlist"/> actions, and only for a user with a tuner,
/// native Live TV permission, after the first sync. Everything else there is denied.</item>
/// <item>Any action anywhere: every identifier in its arguments (route, query, header, and every nested property
/// of body objects of any type) is resolved. Live TV channels, programs and tuner streams must be on the user's
/// tuner; recordings are denied; route content identifiers and token/stream-shaped values must resolve.</item>
/// <item>LiveStreams/Open and LiveStreams/Close must name a resolvable target.</item>
/// </list>
/// This also covers the streaming endpoints, which look items up without a user and can open a tuner themselves
/// (Jellyfin.Api/Helpers/StreamingHelpers.cs:111-130, MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:662-669).
/// Every denial returns the same 403 text. Tuner-limit exceptions become a clean 409.
/// </summary>
public class LiveTvAccessFilter : IAsyncActionFilter
{
    private static readonly Caller AnonymousCaller = new(Guid.Empty, IsApiKey: false, IsAdministrator: false, HasLiveTvAccess: false);

    private readonly AccessService _access;
    private readonly ISyncSignals _sync;
    private readonly ILogger<LiveTvAccessFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvAccessFilter"/> class.
    /// </summary>
    /// <param name="access">The access service.</param>
    /// <param name="sync">The tag-sync worker (readiness, re-sync requests).</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LiveTvAccessFilter}"/> interface.</param>
    public LiveTvAccessFilter(AccessService access, ISyncSignals sync, ILogger<LiveTvAccessFilter> logger)
    {
        _access = access;
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Anonymous callers get default-deny too: Jellyfin does NOT require auth on the stream endpoints
        // (Jellyfin.Api/Controllers/VideosController.cs:312, AudioController.cs:89 have no [Authorize]), so an anonymous
        // request naming a channel would otherwise stream it. They are treated as a caller with no user and no
        // Live TV access; only image requests and the server-internal buffer URLs pass through unchecked.
        var started = Stopwatch.GetTimestamp();
        var caller = _access.GetCaller(context.HttpContext.User);
        var effective = caller ?? AnonymousCaller;
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        if (!AccessPolicy.IsExempt(effective)
            && !(caller is null && LiveTvEndpoints.IsAnonymousPassThrough(action))
            && !(IsLegacyHls(action) ? AdmitLegacyHls(context, action!, effective, caller is null, started) : Admit(context, effective, started)))
        {
            return;
        }

        var executed = await next().ConfigureAwait(false);
        if (!AccessPolicy.IsExempt(effective) && !HideUpstreamUrls(context, executed, effective))
        {
            return;
        }

        if (executed.Exception is not null && !executed.ExceptionHandled && IsTunerConflict(executed.Exception))
        {
            _logger.LogInformation(
                "LiveTvAccess: tuner busy for user={UserId} {Method} {Route}",
                caller?.UserId.ToString("N") ?? "(anonymous)",
                context.HttpContext.Request.Method,
                context.ActionDescriptor.AttributeRouteInfo?.Template ?? "(unknown)");
            executed.Result = PlainText(
                StatusCodes.Status409Conflict,
                "Your tuner is busy with another live stream. Stop it and try again.");
            executed.ExceptionHandled = true;
        }

        // Jellyfin publishes no event for user policy edits (UserManager.UpdatePolicyAsync), so a successful
        // admin / API-key write to /Users/* (policy, user update) requests a users pass instead.
        if (caller is { } admin && AccessPolicy.IsExempt(admin) && IsUserWrite(context) && Succeeded(executed))
        {
            _sync.MarkUsersDirty("user write " + context.ActionDescriptor.AttributeRouteInfo?.Template);
        }
    }

    /// <summary>
    /// Decides a request for a non-exempt caller. Sets a 403 result and returns <c>false</c> on denial.
    /// </summary>
    private bool Admit(ActionExecutingContext context, Caller caller, long started)
    {
        // One snapshot for the whole decision: a concurrent config change can't mix catalog and map.
        var snapshot = _access.Snapshot;
        var ready = _sync.IsReady;
        var action = context.ActionDescriptor as ControllerActionDescriptor;

        if (string.Equals(action?.ControllerName, LiveTvEndpoints.LiveTvController, StringComparison.Ordinal))
        {
            var endpoint = AccessPolicy.DecideEndpoint(caller, snapshot.Map, LiveTvEndpoints.Allowlist.Contains(action!.ActionName), ready);
            if (endpoint != AccessDecision.Allow)
            {
                return Deny(context, caller, endpoint, "endpoint", action.ActionName, started, 0);
            }
        }

        if (!TargetCollector.TryCollect(context, out var values))
        {
            return Deny(context, caller, AccessDecision.DenyUnresolved, "request", "too large or deep to inspect", started, values.Count);
        }

        var resolvedTarget = false;
        var namesLiveTvItem = false;
        var tracked = false;
        foreach (var value in values)
        {
            var target = _access.Classify(snapshot, value, LiveTvEndpoints.IsOpening(action));
            var decision = AccessPolicy.DecideTarget(caller, snapshot.Map, target, ready);
            if (decision != AccessDecision.Allow)
            {
                return Deny(context, caller, decision, "target", value.Guid is null ? Redact.ShortHash(value.Text) : value.Text, started, values.Count);
            }

            // A resolved Live TV target, or a resolved stream/token of another provider.
            resolvedTarget |= target.Kind == TargetKind.LiveTv || (target.Kind == TargetKind.NotLiveTv && value.Guid is null);
            if (target.Kind == TargetKind.LiveTv)
            {
                var isTracked = _access.IsTrackedLiveStream(value);
                tracked |= isTracked;
                namesLiveTvItem |= !isTracked;
            }
        }

        // A static stream of a channel without an open live stream: Jellyfin would proxy the provider directly,
        // never opening (or counting) a tuner stream (VideosController.cs:456-460, AudioHelper.cs:116-119).
        var isStatic = LiveTvEndpoints.IsStaticStream(action, context.ActionArguments);
        var staticDecision = AccessPolicy.DecideStatic(caller, isStatic, namesLiveTvItem, tracked);
        if (staticDecision != AccessDecision.Allow)
        {
            return Deny(context, caller, staticDecision, "static stream", action?.ActionName ?? "(unknown)", started, values.Count);
        }

        if (action is not null && !resolvedTarget && LiveTvEndpoints.RequireTarget.Contains((action.ControllerName, action.ActionName)))
        {
            return Deny(context, caller, AccessDecision.DenyMissingTarget, "endpoint", action.ActionName, started, values.Count);
        }

        if (values.Count > 0)
        {
            LogCheckTime(started, values.Count, denied: false);
        }

        return true;
    }

    /// <summary>
    /// Replaces a response that carries Live TV upstream URLs (media source Path / EncoderPath / headers, Live TV
    /// config) with a scrubbed copy. If the copy can't be made the response is denied (fail closed).
    /// </summary>
    private bool HideUpstreamUrls(ActionExecutingContext context, ActionExecutedContext executed, Caller caller)
    {
        if (executed.Exception is not null || executed.Result is not ObjectResult { Value: { } value } result)
        {
            return true;
        }

        try
        {
            var scrubbed = ResponseScrubber.Scrub(value);
            if (!ReferenceEquals(scrubbed, value))
            {
                executed.Result = new ObjectResult(scrubbed)
                {
                    StatusCode = result.StatusCode,
                    DeclaredType = result.DeclaredType,
                    ContentTypes = result.ContentTypes,
                    Formatters = result.Formatters,
                };
            }

            return true;
        }
#pragma warning disable CA1031 // Could not scrub: don't send the original. Fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "LiveTvAccess: could not hide upstream URLs in a response; denying it");
            executed.Result = PlainText(StatusCodes.Status403Forbidden, AccessPolicy.DeniedMessage);
            return false;
        }
    }

    private static bool IsLegacyHls(ControllerActionDescriptor? action)
        => action is not null
            && string.Equals(action.ControllerName, LiveTvEndpoints.LegacyHlsController, StringComparison.Ordinal)
            && LiveTvEndpoints.LegacyHlsActions.Contains(action.ActionName);

    /// <summary>
    /// Legacy HLS file routes serve "{transcode folder}/{name}{ext}" by name. Only a running job's own files may be
    /// served: names must be plain stems, the job is found by its exact playlist path, the segment must be exactly
    /// one of that job's segment names, and the route's item id must resolve. Logged-in non-admins also get the
    /// tuner check on the item and on the job's live stream. Anonymous requests for a running job are allowed
    /// (accepted risk A1: some browsers fetch segments without auth; the random job name is the only secret).
    /// </summary>
    private bool AdmitLegacyHls(ActionExecutingContext context, ControllerActionDescriptor action, Caller caller, bool anonymous, long started)
    {
        var snapshot = _access.Snapshot;
        var ready = _sync.IsReady;
        string? Arg(string name) => context.ActionArguments.TryGetValue(name, out var v) ? v as string : null;
        bool Refuse(string detail) => Deny(context, caller, AccessDecision.DenyUnresolved, "legacy-hls", detail, started, 0);

        // The route item id must be a real item (Jellyfin itself ignores it).
        if (!ProviderString.TryParseGuid(Arg("itemId"), out var itemId) || itemId == Guid.Empty)
        {
            return Refuse("item id");
        }

        var item = _access.Classify(snapshot, new CollectedValue(itemId.ToString("N"), itemId, IsRouteIdentifier: true));
        if (item.Kind is TargetKind.Unresolved or TargetKind.Recording)
        {
            return Deny(context, caller, AccessPolicy.DecideTarget(caller, snapshot.Map, item, ready), "legacy-hls", "item", started, 1);
        }

        if (!anonymous && AccessPolicy.DecideTarget(caller, snapshot.Map, item, ready) is var itemDecision && itemDecision != AccessDecision.Allow)
        {
            return Deny(context, caller, itemDecision, "legacy-hls", "item", started, 1);
        }

        // The file: exactly one of a running job's own files.
        MediaBrowser.Controller.MediaEncoding.TranscodingJob? job = null;
        switch (action.ActionName)
        {
            case "GetHlsVideoSegmentLegacy":
                var playlistId = Arg("playlistId");
                var segmentId = Arg("segmentId");
                var container = Arg("segmentContainer");
                if (!HlsNames.IsSafeName(playlistId) || !HlsNames.IsSafeName(segmentId)
                    || container is null || !HlsNames.SegmentExtensions.Contains(container)
                    || !HlsNames.IsSegmentOf(playlistId!, segmentId!))
                {
                    return Refuse("segment name");
                }

                job = _access.FindHlsJob(playlistId!);
                break;

            case "GetHlsAudioSegmentLegacy":
                var audioSegment = Arg("segmentId");
                if (!HlsNames.IsSafeName(audioSegment))
                {
                    return Refuse("segment name");
                }

                foreach (var candidate in HlsNames.CandidateJobNames(audioSegment!))
                {
                    job = _access.FindHlsJob(candidate);
                    if (job is not null)
                    {
                        break;
                    }
                }

                break;

            default: // GetHlsPlaylistLegacy
                var playlist = Arg("playlistId");
                if (!HlsNames.IsSafeName(playlist))
                {
                    return Refuse("playlist name");
                }

                job = _access.FindHlsJob(playlist!);
                break;
        }

        if (job is null)
        {
            return Refuse("no running job");
        }

        // The job's own stream: tuner check for logged-in callers; anonymous may use any running job (A1).
        var stream = _access.ClassifyJob(snapshot, job);
        if (stream.Kind is TargetKind.Unresolved or TargetKind.Recording)
        {
            return Deny(context, caller, AccessDecision.DenyUnresolved, "legacy-hls", "job stream", started, 1);
        }

        if (!anonymous && AccessPolicy.DecideTarget(caller, snapshot.Map, stream, ready) is var streamDecision && streamDecision != AccessDecision.Allow)
        {
            return Deny(context, caller, streamDecision, "legacy-hls", "job stream", started, 1);
        }

        return true;
    }

    private bool Deny(ActionExecutingContext context, Caller caller, AccessDecision decision, string what, string detail, long started, int targets)
    {
        _logger.LogInformation(
            "LiveTvAccess: denied {Decision} user={UserId} {Method} {Route} {What}={Detail}",
            decision,
            caller.UserId == Guid.Empty ? "(anonymous)" : caller.UserId.ToString("N"),
            context.HttpContext.Request.Method,
            context.ActionDescriptor.AttributeRouteInfo?.Template ?? context.ActionDescriptor.DisplayName ?? "(unknown)",
            what,
            detail);
        context.Result = PlainText(StatusCodes.Status403Forbidden, AccessPolicy.Message(decision));
        LogCheckTime(started, targets, denied: true);
        return false;
    }

    private void LogCheckTime(long started, int targets, bool denied)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "LiveTvAccess: check took {Microseconds} us for {Targets} target(s), denied={Denied}",
                (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds,
                targets,
                denied);
        }
    }

    private static bool IsUserWrite(ActionExecutingContext context)
    {
        var template = context.ActionDescriptor.AttributeRouteInfo?.Template?.TrimStart('/') ?? string.Empty;
        return !HttpMethods.IsGet(context.HttpContext.Request.Method)
            && !HttpMethods.IsHead(context.HttpContext.Request.Method)
            && (template.Equals("Users", StringComparison.OrdinalIgnoreCase)
                || template.StartsWith("Users/", StringComparison.OrdinalIgnoreCase))
            && !template.Contains("Authenticate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Succeeded(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        var status = (executed.Result as IStatusCodeActionResult)?.StatusCode;
        return status is null or < 400;
    }

    private static bool IsTunerConflict(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is LiveTvConflictException)
            {
                return true;
            }
        }

        return false;
    }

    private static ContentResult PlainText(int statusCode, string message) => new()
    {
        StatusCode = statusCode,
        Content = message,
        ContentType = "text/plain; charset=utf-8",
    };
}
