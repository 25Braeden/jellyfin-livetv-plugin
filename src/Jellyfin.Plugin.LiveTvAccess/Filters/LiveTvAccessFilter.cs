using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LiveTvAccess.Core;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Global MVC action filter that enforces per-user tuner access before Jellyfin does any work.
/// <para>
/// Instead of a list of endpoints it inspects every authenticated request for anything that names a
/// Live TV channel or stream: an item/channel/program id (PlaybackInfo, /Videos/{itemId}/stream, HLS,
/// channel details), a live stream id (streaming with an open stream, LiveStreams/Close) or an open
/// token (LiveStreams/Open). This also covers the streaming endpoints, which look items up without a
/// user and can open a tuner themselves (Jellyfin.Api/Helpers/StreamingHelpers.cs:111-130,
/// MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:662-669).
/// </para>
/// It also turns Jellyfin's tuner-limit exception into a clean 409 instead of a generic 500.
/// </summary>
public class LiveTvAccessFilter : IAsyncActionFilter
{
    // Action argument names (Jellyfin 10.11.11 controllers). Version-fragile.
    private static readonly string[] _itemIdArguments = ["itemId", "channelId", "programId"];

    // Properties read from request bodies (PlaybackInfoDto, OpenLiveStreamDto; Jellyfin.Api, not on NuGet).
    private static readonly string[] _bodyProperties = ["ItemId", "LiveStreamId", "OpenToken"];

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

    private enum TargetKind
    {
        Item,
        LiveStream,
        OpenToken,
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Anonymous requests: Jellyfin's own authorization decides (streaming endpoints require auth).
        var started = Stopwatch.GetTimestamp();
        var caller = _access.GetCaller(context.HttpContext.User);
        if (caller is { } c && !AccessPolicy.IsExempt(c))
        {
            // One snapshot for the whole decision: a concurrent config change can't mix catalog and map.
            var snapshot = _access.Snapshot;
            var targets = 0;
            foreach (var (kind, value) in GetTargets(context.ActionArguments))
            {
                targets++;
                var (decision, isLiveTv) = Check(c, snapshot, kind, value);
                if (!isLiveTv || decision == AccessDecision.Allow)
                {
                    continue;
                }

                _logger.LogInformation(
                    "LiveTvAccess: denied {Decision} user={UserId} {Method} {Route} {Kind}={Target}",
                    decision,
                    c.UserId.ToString("N"),
                    context.HttpContext.Request.Method,
                    context.ActionDescriptor.AttributeRouteInfo?.Template ?? "(unknown)",
                    kind,
                    kind == TargetKind.Item ? value : Redact.ShortHash(value));
                context.Result = PlainText(StatusCodes.Status403Forbidden, AccessPolicy.Message(decision));
                LogCheckTime(started, targets, denied: true);
                return;
            }

            if (targets > 0)
            {
                LogCheckTime(started, targets, denied: false);
            }
        }

        var executed = await next().ConfigureAwait(false);
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

    private static IEnumerable<(TargetKind Kind, string Value)> GetTargets(IDictionary<string, object?> arguments)
    {
        foreach (var (name, value) in arguments)
        {
            if (value is null)
            {
                continue;
            }

            if (Array.Exists(_itemIdArguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (value is Guid id)
                {
                    yield return (TargetKind.Item, id.ToString("N"));
                }
                else if (value is string s && Guid.TryParse(s, out var parsed))
                {
                    yield return (TargetKind.Item, parsed.ToString("N"));
                }
            }
            else if (string.Equals(name, "liveStreamId", StringComparison.OrdinalIgnoreCase) && value is string ls && ls.Length > 0)
            {
                yield return (TargetKind.LiveStream, ls);
            }
            else if (string.Equals(name, "openToken", StringComparison.OrdinalIgnoreCase) && value is string token && token.Length > 0)
            {
                yield return (TargetKind.OpenToken, token);
            }
            else if (value.GetType() is { IsClass: true } type && type != typeof(string) && type.Namespace?.StartsWith("Jellyfin.Api", StringComparison.Ordinal) == true)
            {
                // Request bodies, read by name (types live in Jellyfin.Api, which is not on NuGet).
                foreach (var property in _bodyProperties)
                {
                    var v = type.GetProperty(property)?.GetValue(value);
                    switch (property, v)
                    {
                        case ("ItemId", Guid g) when g != Guid.Empty:
                            yield return (TargetKind.Item, g.ToString("N"));
                            break;
                        case ("LiveStreamId", string s) when s.Length > 0:
                            yield return (TargetKind.LiveStream, s);
                            break;
                        case ("OpenToken", string s) when s.Length > 0:
                            yield return (TargetKind.OpenToken, s);
                            break;
                    }
                }
            }
        }
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

    private (AccessDecision Decision, bool IsLiveTv) Check(Caller caller, AccessSnapshot snapshot, TargetKind kind, string value)
    {
        string? tuner;
        bool isLiveTv;
        switch (kind)
        {
            case TargetKind.Item:
                tuner = _access.GetItemTuner(snapshot, Guid.ParseExact(value, "N"), out isLiveTv);
                break;
            case TargetKind.LiveStream:
                tuner = _access.GetLiveStreamTuner(snapshot, value, out isLiveTv);
                break;
            default:
                // Only Live TV provider tokens can reach a tuner (MediaSourceManager.cs:886-899 dispatches
                // by prefix). A Live TV token we cannot parse, or that names a non-channel, is denied.
                tuner = JellyfinIds.TryParseLiveTvOpenToken(value, out isLiveTv, out var itemId)
                    ? _access.GetItemTuner(snapshot, itemId, out _)
                    : null;
                break;
        }

        return (AccessPolicy.Decide(caller, snapshot.Map, tuner, _sync.IsReady), isLiveTv);
    }
}
