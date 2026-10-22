using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using LiveTvAccess.Core;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Hides upstream (provider) URLs from non-admins. Live TV media sources carry the raw provider stream URL in
/// <c>Path</c> (and possibly <c>EncoderPath</c> and auth headers in <c>RequiredHttpHeaders</c>); the Live TV
/// configuration carries the tuner M3U URLs and guide provider credentials, and
/// <c>GET /System/Configuration/livetv</c> is readable by any logged-in user
/// (Jellyfin.Api/Controllers/ConfigurationController.cs:78). Clients play through <c>TranscodingUrl</c> /
/// the server stream endpoints (direct play is off when a tuner has a stream limit, M3UTunerHost.cs:148), so they
/// don't need these values. Responses are deep-copied before scrubbing: some DTOs are shared server state.
/// </summary>
public static class ResponseScrubber
{
    private static readonly HashSet<BaseItemKind> _liveTvKinds =
    [
        BaseItemKind.TvChannel, BaseItemKind.LiveTvChannel, BaseItemKind.LiveTvProgram, BaseItemKind.Program,
        BaseItemKind.TvProgram, BaseItemKind.Recording,
    ];

    /// <summary>
    /// Gets whether the scrubber handles a response type.
    /// </summary>
    /// <param name="type">The response type.</param>
    /// <returns><c>true</c> if handled.</returns>
    public static bool Supports(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return typeof(LiveTvOptions).IsAssignableFrom(type)
            || typeof(PlaybackInfoResponse).IsAssignableFrom(type)
            || typeof(LiveStreamResponse).IsAssignableFrom(type)
            || typeof(BaseItemDto).IsAssignableFrom(type)
            || typeof(SessionInfoDto).IsAssignableFrom(type)
            || typeof(QueryResult<BaseItemDto>).IsAssignableFrom(type)
            || typeof(IEnumerable<BaseItemDto>).IsAssignableFrom(type)
            || typeof(IEnumerable<SessionInfoDto>).IsAssignableFrom(type);
    }

    /// <summary>
    /// Gets the response type an action must return on success, if its responses can carry Live TV media sources
    /// or the Live TV configuration: the actions with a response requirement in <see cref="Health.EndpointContract"/>,
    /// and <c>GetNamedConfiguration</c> for the <c>livetv</c> key. Anything else from them is refused.
    /// </summary>
    /// <param name="controller">The controller name.</param>
    /// <param name="action">The action name.</param>
    /// <param name="arguments">The action arguments.</param>
    /// <param name="exact">The exact type required (the Live TV configuration), or <c>null</c> for any handled shape.</param>
    /// <returns><c>true</c> if the action's success responses are checked.</returns>
    public static bool IsSensitive(string? controller, string? action, IDictionary<string, object?> arguments, out Type? exact)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exact = null;
        if (controller == "Configuration" && action == "GetNamedConfiguration")
        {
            var key = arguments.FirstOrDefault(a => string.Equals(a.Key, "key", StringComparison.OrdinalIgnoreCase)).Value as string;
            exact = typeof(LiveTvOptions);
            return string.Equals(key?.Trim(), "livetv", StringComparison.OrdinalIgnoreCase);
        }

        return Health.EndpointContract.Requirements.Any(r => r.Returns is not null && r.Controller == controller && r.Action == action);
    }

    /// <summary>
    /// Returns a scrubbed deep copy of a response value, or the same instance if there is nothing to hide.
    /// </summary>
    /// <param name="value">The response value.</param>
    /// <returns>The value to send.</returns>
    /// <exception cref="JsonException">If the value can't be copied (callers fail closed).</exception>
    public static object Scrub(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Visit(value, scrub: false))
        {
            return value;
        }

        var type = value.GetType();
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, type, JsonDefaults.Options), type, JsonDefaults.Options)
            ?? throw new JsonException("Response copy was null.");
        Visit(copy, scrub: true);
        return copy;
    }

    /// <summary>
    /// Walks the response shapes that can carry media sources or the Live TV configuration. With
    /// <paramref name="scrub"/> false it only reports whether anything needs hiding.
    /// </summary>
    private static bool Visit(object value, bool scrub)
    {
        switch (value)
        {
            case LiveTvOptions options:
                return ScrubOptions(options, scrub);
            case PlaybackInfoResponse info:
                return Sources(info.MediaSources, inLiveTvItem: false, scrub);
            case LiveStreamResponse live:
                return live.MediaSource is not null && Source(live.MediaSource, inLiveTvItem: false, scrub);
            case BaseItemDto dto:
                return Item(dto, scrub);
            case SessionInfoDto session:
                return Session(session, scrub);
            case QueryResult<BaseItemDto> query:
                return Any(query.Items, scrub);
            case IEnumerable<BaseItemDto> items:
                return Any(items, scrub);
            case IEnumerable<SessionInfoDto> sessions:
                return sessions.Aggregate(false, (found, s) => Session(s, scrub) | found);
            default:
                return false;
        }
    }

    private static bool Any(IEnumerable<BaseItemDto>? items, bool scrub)
        => items?.Aggregate(false, (found, dto) => Item(dto, scrub) | found) ?? false;

    private static bool Session(SessionInfoDto session, bool scrub)
        => (session.NowPlayingItem is { } now && Item(now, scrub))
            | (session.NowViewingItem is { } viewing && Item(viewing, scrub));

    private static bool Item(BaseItemDto dto, bool scrub)
        => Sources(dto.MediaSources, dto.Type is { } kind && _liveTvKinds.Contains(kind), scrub);

    private static bool Sources(IEnumerable<MediaSourceInfo>? sources, bool inLiveTvItem, bool scrub)
        => sources?.Aggregate(false, (found, s) => Source(s, inLiveTvItem, scrub) | found) ?? false;

    private static bool Source(MediaSourceInfo source, bool inLiveTvItem, bool scrub)
    {
        var isLiveTv = inLiveTvItem
            || IsLiveTvPrefixed(source.OpenToken)
            || IsLiveTvPrefixed(source.LiveStreamId)
            || (source.Path?.Contains("/LiveTv/LiveStreamFiles/", StringComparison.OrdinalIgnoreCase) ?? false);
        var carriesUrl = !string.IsNullOrEmpty(source.Path)
            || !string.IsNullOrEmpty(source.EncoderPath)
            || source.RequiredHttpHeaders is { Count: > 0 };
        if (!isLiveTv || !carriesUrl)
        {
            return false;
        }

        if (scrub)
        {
            source.Path = null;
            source.EncoderPath = null;
            source.RequiredHttpHeaders = new Dictionary<string, string>();
        }

        return true;
    }

    private static bool ScrubOptions(LiveTvOptions options, bool scrub)
    {
        var found = false;
        foreach (var host in options.TunerHosts ?? [])
        {
            found |= !string.IsNullOrEmpty(host.Url) || !string.IsNullOrEmpty(host.Source);
            if (scrub)
            {
                host.Url = string.Empty;
                host.Source = string.Empty;
            }
        }

        foreach (var provider in options.ListingProviders ?? [])
        {
            found |= !string.IsNullOrEmpty(provider.Path) || !string.IsNullOrEmpty(provider.Username)
                || !string.IsNullOrEmpty(provider.Password) || !string.IsNullOrEmpty(provider.ListingsId);
            if (scrub)
            {
                provider.Path = string.Empty;
                provider.Username = string.Empty;
                provider.Password = string.Empty;
                provider.ListingsId = string.Empty;
            }
        }

        return found;
    }

    private static bool IsLiveTvPrefixed(string? value)
        => value is not null && value.StartsWith(JellyfinIds.LiveTvProviderPrefix + "_", StringComparison.OrdinalIgnoreCase);
}
