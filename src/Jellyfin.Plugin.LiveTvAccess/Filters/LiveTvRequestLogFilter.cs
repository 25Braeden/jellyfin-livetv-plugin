using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Gate 2 probe: logs who calls the Live TV endpoints we will later police. Never blocks.
/// Registered globally via MvcOptions (see <see cref="PluginServiceRegistrator"/>).
/// </summary>
public class LiveTvRequestLogFilter : IAsyncActionFilter
{
    // Claim names set by Jellyfin.Api/Auth/CustomAuthenticationHandler.cs:62-70
    // (constants in Jellyfin.Api/Constants/InternalClaimTypes.cs, not shipped on NuGet). Version-fragile.
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly ILogger<LiveTvRequestLogFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvRequestLogFilter"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{LiveTvRequestLogFilter}"/> interface.</param>
    public LiveTvRequestLogFilter(ILogger<LiveTvRequestLogFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var endpoint = LiveTvEndpoints.Match(context);
        if (endpoint is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var user = context.HttpContext.User;
        var userId = user.FindFirstValue(UserIdClaim) ?? "(none)";
        var userName = user.FindFirstValue(ClaimTypes.Name) ?? "(none)";
        var isApiKey = user.FindFirstValue(IsApiKeyClaim) ?? "(none)";
        var route = context.ActionDescriptor.AttributeRouteInfo?.Template ?? "(unknown)";

        if (endpoint == LiveTvEndpoint.Channels)
        {
            var executed = await next().ConfigureAwait(false);
            var ids = (executed.Result as ObjectResult)?.Value is QueryResult<BaseItemDto> result
                ? string.Join(',', result.Items.Select(i => i.Id.ToString("N")))
                : "(no result)";
            _logger.LogInformation(
                "LiveTvAccess probe: {Method} {Route} user={UserId} name={UserName} apiKey={IsApiKey} channels=[{ChannelIds}]",
                context.HttpContext.Request.Method,
                route,
                userId,
                userName,
                isApiKey,
                ids);
            return;
        }

        _logger.LogInformation(
            "LiveTvAccess probe: {Method} {Route} user={UserId} name={UserName} apiKey={IsApiKey} item={ItemId} openToken={OpenToken}",
            context.HttpContext.Request.Method,
            route,
            userId,
            userName,
            isApiKey,
            LiveTvEndpoints.GetItemId(context) ?? "(none)",
            LiveTvEndpoints.GetOpenToken(context) ?? "(none)");
        await next().ConfigureAwait(false);
    }
}
