using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Jellyfin 10.11.11 endpoint facts the filter relies on (version-fragile; checked by name).
/// </summary>
public static class LiveTvEndpoints
{
    /// <summary>Controller name of Jellyfin.Api/Controllers/LiveTvController.cs.</summary>
    public const string LiveTvController = "LiveTv";

    /// <summary>Controller of the legacy HLS file routes (Jellyfin.Api/Controllers/HlsSegmentController.cs).</summary>
    public const string LegacyHlsController = "HlsSegment";

    /// <summary>
    /// LiveTvController actions a mapped, non-admin user may call. Everything else on LiveTvController (timers,
    /// series timers, recordings, tuner and listing-provider management, channel mappings, raw stream files) is
    /// denied for non-admins.
    /// </summary>
    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetLiveTvInfo",          // GET LiveTv/Info
        "GetLiveTvChannels",      // GET LiveTv/Channels
        "GetChannel",             // GET LiveTv/Channels/{channelId}
        "GetLiveTvPrograms",      // GET LiveTv/Programs
        "GetPrograms",            // POST LiveTv/Programs
        "GetRecommendedPrograms", // GET LiveTv/Programs/Recommended ("On Now")
        "GetProgram",             // GET LiveTv/Programs/{programId}
        "GetGuideInfo",           // GET LiveTv/GuideInfo
    };

    /// <summary>
    /// Actions that must name a resolvable target (Jellyfin.Api/Controllers/MediaInfoController.cs:269, :314);
    /// without one the request is denied.
    /// </summary>
    public static readonly IReadOnlySet<(string Controller, string Action)> RequireTarget = new HashSet<(string, string)>
    {
        ("MediaInfo", "OpenLiveStream"),
        ("MediaInfo", "CloseLiveStream"),
    };

    /// <summary>
    /// Route parameter names that identify content and must resolve. Others (userId, sessionId, pluginId,
    /// mediaSourceId, segmentId, and HLS playlistId, which is a 32-hex value that is not an item) are inspected
    /// like any argument but needn't resolve.
    /// </summary>
    public static readonly IReadOnlySet<string> RouteIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "itemId", "routeItemId", "channelId", "programId", "recordingId", "seriesId", "videoId",
        "collectionId", "streamId", "timerId",
    };

    /// <summary>
    /// Legacy HLS actions that serve files from the transcode folder by name. Clients still use the video segment
    /// route for live (event) playlists, whose segment URLs are "hls/{playlist}/" (DynamicHlsController.cs:1656-1662).
    /// They are bound to a running job instead of being denied.
    /// </summary>
    public static readonly IReadOnlySet<string> LegacyHlsActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetHlsVideoSegmentLegacy", // GET Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer} (anonymous)
        "GetHlsAudioSegmentLegacy", // GET Audio/{itemId}/hls/{segmentId}/stream.mp3|aac (anonymous)
        "GetHlsPlaylistLegacy",     // GET Videos/{itemId}/hls/{playlistId}/stream.m3u8 ([Authorize])
    };

    /// <summary>
    /// Gets whether an anonymous request to this action skips the checks:
    /// <list type="bullet">
    /// <item>ImageController (accepted, user decision: clients load images without auth; channel logos and
    /// programme art are metadata only).</item>
    /// <item>The server-internal buffer URLs Jellyfin's own ffmpeg reads without auth (LiveTvController.cs:1162,
    /// :1189). Their ids are random; restricting them is fix 6.</item>
    /// </list>
    /// </summary>
    /// <param name="action">The action, if it is a controller action.</param>
    /// <returns><c>true</c> to let an anonymous request through unchecked.</returns>
    public static bool IsAnonymousPassThrough(Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor? action)
    {
        return action is not null
            && (string.Equals(action.ControllerName, "Image", StringComparison.Ordinal)
                || (string.Equals(action.ControllerName, LiveTvController, StringComparison.Ordinal)
                    && action.ActionName is "GetLiveStreamFile" or "GetLiveRecordingFile"));
    }

    /// <summary>
    /// Gets whether an action can open (and so share) a Live TV stream: playback info / open, and the streaming
    /// controllers, which open the channel's stream themselves (TranscodeManager.cs:662-669).
    /// </summary>
    /// <param name="action">The action, if it is a controller action.</param>
    /// <returns><c>true</c> if it can open a stream.</returns>
    public static bool IsOpening(Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor? action)
        => action?.ControllerName is "MediaInfo" or "Videos" or "Audio" or "DynamicHls" or "UniversalAudio";

    /// <summary>
    /// Gets whether a request is, or may be served as, a static (direct) stream. Videos / Audio stream actions take
    /// <c>static</c>, also settable through field 3 of the legacy <c>params</c> string (StreamingHelpers.cs:405-421);
    /// UniversalAudio decides itself and may stream statically or redirect (UniversalAudioController.cs:176-182).
    /// </summary>
    /// <param name="action">The action, if it is a controller action.</param>
    /// <param name="arguments">The action arguments.</param>
    /// <returns><c>true</c> if it may be static.</returns>
    public static bool IsStaticStream(Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor? action, IDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (action?.ControllerName is "UniversalAudio")
        {
            return true;
        }

        if (action?.ControllerName is not ("Videos" or "Audio"))
        {
            return false;
        }

        foreach (var (name, value) in arguments)
        {
            if (string.Equals(name, "static", StringComparison.OrdinalIgnoreCase) && value is true)
            {
                return true;
            }

            if (string.Equals(name, "params", StringComparison.OrdinalIgnoreCase) && value is string legacy)
            {
                var fields = legacy.Split(';');
                if (fields.Length > 3 && string.Equals(fields[3], "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
