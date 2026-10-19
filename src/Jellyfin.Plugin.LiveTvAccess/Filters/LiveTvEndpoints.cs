using System;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// The core Jellyfin endpoints the plugin watches.
/// </summary>
public enum LiveTvEndpoint
{
    /// <summary>POST Items/{itemId}/PlaybackInfo (Jellyfin.Api/Controllers/MediaInfoController.cs:116).</summary>
    PlaybackInfo,

    /// <summary>POST LiveStreams/Open (MediaInfoController.cs:269).</summary>
    OpenLiveStream,

    /// <summary>GET LiveTv/Channels (Jellyfin.Api/Controllers/LiveTvController.cs:145).</summary>
    Channels,
}

/// <summary>
/// Matches requests to <see cref="LiveTvEndpoint"/> by HTTP method and attribute-route template.
/// Templates are pinned to Jellyfin 10.11.11 and are version-fragile.
/// </summary>
public static class LiveTvEndpoints
{
    /// <summary>
    /// Gets the watched endpoint for a request, if any.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <returns>The endpoint, or <c>null</c> if the request is not one we watch.</returns>
    public static LiveTvEndpoint? Match(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = context.HttpContext.Request.Method;
        var template = context.ActionDescriptor.AttributeRouteInfo?.Template;
        if (template is null)
        {
            return null;
        }

        if (HttpMethodIs(method, "POST") && TemplateIs(template, "Items/{itemId}/PlaybackInfo"))
        {
            return LiveTvEndpoint.PlaybackInfo;
        }

        if (HttpMethodIs(method, "POST") && TemplateIs(template, "LiveStreams/Open"))
        {
            return LiveTvEndpoint.OpenLiveStream;
        }

        if (HttpMethodIs(method, "GET") && TemplateIs(template, "LiveTv/Channels"))
        {
            return LiveTvEndpoint.Channels;
        }

        return null;
    }

    /// <summary>
    /// Gets the item id from the action arguments: route/query <c>itemId</c>, else the open-live-stream body.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <returns>The item id ("N" format), or <c>null</c>.</returns>
    public static string? GetItemId(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActionArguments.TryGetValue("itemId", out var value) && value is Guid id && id != Guid.Empty)
        {
            return id.ToString("N");
        }

        return GetBodyProperty(context, "ItemId") is Guid bodyId && bodyId != Guid.Empty
            ? bodyId.ToString("N")
            : null;
    }

    /// <summary>
    /// Gets the open token from the query or the open-live-stream body.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <returns>The open token, or <c>null</c>.</returns>
    public static string? GetOpenToken(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActionArguments.TryGetValue("openToken", out var value) && value is string token && token.Length > 0)
        {
            return token;
        }

        return GetBodyProperty(context, "OpenToken") as string;
    }

    /// <summary>
    /// Reads a property of the LiveStreams/Open body by name. The body type (OpenLiveStreamDto,
    /// Jellyfin.Api/Models/MediaInfoDtos/OpenLiveStreamDto.cs) is not on NuGet, hence reflection. Version-fragile.
    /// </summary>
    private static object? GetBodyProperty(ActionExecutingContext context, string name)
    {
        return context.ActionArguments.TryGetValue("openLiveStreamDto", out var dto) && dto is not null
            ? dto.GetType().GetProperty(name)?.GetValue(dto)
            : null;
    }

    private static bool HttpMethodIs(string method, string expected)
        => string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TemplateIs(string template, string expected)
        => string.Equals(template.TrimStart('/'), expected, StringComparison.OrdinalIgnoreCase);
}
