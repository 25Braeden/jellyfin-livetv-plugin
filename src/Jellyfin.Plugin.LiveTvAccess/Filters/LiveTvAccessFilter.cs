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

        // Anonymous requests: Jellyfin's own authorization decides (streaming endpoints require auth).
        var started = Stopwatch.GetTimestamp();
        var caller = _access.GetCaller(context.HttpContext.User);
        if (caller is { } c && !AccessPolicy.IsExempt(c) && !Admit(context, c, started))
        {
            return;
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
        foreach (var value in values)
        {
            var target = _access.Classify(snapshot, value);
            var decision = AccessPolicy.DecideTarget(caller, snapshot.Map, target, ready);
            if (decision != AccessDecision.Allow)
            {
                return Deny(context, caller, decision, "target", value.Guid is null ? Redact.ShortHash(value.Text) : value.Text, started, values.Count);
            }

            // A resolved Live TV target, or a resolved stream/token of another provider.
            resolvedTarget |= target.Kind == TargetKind.LiveTv || (target.Kind == TargetKind.NotLiveTv && value.Guid is null);
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

    private bool Deny(ActionExecutingContext context, Caller caller, AccessDecision decision, string what, string detail, long started, int targets)
    {
        _logger.LogInformation(
            "LiveTvAccess: denied {Decision} user={UserId} {Method} {Route} {What}={Detail}",
            decision,
            caller.UserId.ToString("N"),
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
