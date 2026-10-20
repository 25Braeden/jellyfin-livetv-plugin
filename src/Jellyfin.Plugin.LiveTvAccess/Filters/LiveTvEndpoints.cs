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
}
