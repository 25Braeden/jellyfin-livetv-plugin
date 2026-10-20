using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// The Jellyfin 10.11.11 endpoint shapes the filter and scrubber rely on (version-fragile), and the check against the
/// running server. Parameter names matter where the filter reads an argument by name (<c>static</c>, <c>params</c>,
/// <c>playSessionId</c>, route identifiers); action names where it decides by action; return types where the
/// scrubber recognizes the response.
/// </summary>
public static class EndpointContract
{
    private static readonly string[] _stream = ["itemId", "static", "params", "liveStreamId", "playSessionId", "mediaSourceId"];

    /// <summary>Gets the requirements.</summary>
    public static IReadOnlyList<EndpointRequirement> Requirements { get; } =
    [
        // LiveTvController: the allowlist, and the buffer URLs (Jellyfin.Api/Controllers/LiveTvController.cs).
        new("LiveTv", "GetLiveTvInfo", []),
        new("LiveTv", "GetLiveTvChannels", [], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetChannel", ["channelId"], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetLiveTvPrograms", [], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetPrograms", [], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetRecommendedPrograms", [], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetProgram", ["programId"], Returns: typeof(BaseItemDto)),
        new("LiveTv", "GetGuideInfo", []),
        new("LiveTv", "GetLiveStreamFile", ["streamId", "container"]),
        new("LiveTv", "GetLiveRecordingFile", ["recordingId"]),

        // Playback info and open/close (MediaInfoController.cs).
        new("MediaInfo", "GetPlaybackInfo", ["itemId"], Returns: typeof(PlaybackInfoResponse)),
        new("MediaInfo", "GetPostedPlaybackInfo", ["itemId", "liveStreamId", "mediaSourceId"], ["LiveStreamId", "MediaSourceId"], typeof(PlaybackInfoResponse)),
        new("MediaInfo", "OpenLiveStream", ["openToken", "itemId", "playSessionId"], ["OpenToken"], typeof(LiveStreamResponse)),
        new("MediaInfo", "CloseLiveStream", ["liveStreamId"]),

        // Streaming (VideosController.cs, AudioController.cs, UniversalAudioController.cs, DynamicHlsController.cs).
        new("Videos", "GetVideoStream", _stream),
        new("Videos", "GetVideoStreamByContainer", [.. _stream, "container"]),
        new("Audio", "GetAudioStream", _stream),
        new("Audio", "GetAudioStreamByContainer", [.. _stream, "container"]),
        new("UniversalAudio", "GetUniversalAudioStream", ["itemId"]),
        new("DynamicHls", "GetMasterHlsVideoPlaylist", ["itemId", "params", "playSessionId", "liveStreamId", "mediaSourceId"]),
        new("DynamicHls", "GetVariantHlsVideoPlaylist", ["itemId", "params", "playSessionId", "liveStreamId", "mediaSourceId"]),
        new("DynamicHls", "GetHlsVideoSegment", ["itemId", "playlistId", "segmentId", "params", "playSessionId", "liveStreamId"]),

        // Legacy HLS file routes (HlsSegmentController.cs).
        new("HlsSegment", "GetHlsVideoSegmentLegacy", ["itemId", "playlistId", "segmentId", "segmentContainer"]),
        new("HlsSegment", "GetHlsAudioSegmentLegacy", ["itemId", "segmentId"]),
        new("HlsSegment", "GetHlsPlaylistLegacy", ["itemId", "playlistId"]),
        new("HlsSegment", "StopEncodingProcess", ["deviceId", "playSessionId"]),

        // Responses the scrubber covers, and the user-policy write that triggers a re-sync.
        new("Configuration", "GetNamedConfiguration", ["key"]),
        new("Session", "GetSessions", [], Returns: typeof(SessionInfoDto)),
        new("User", "UpdateUserPolicy", ["userId"], TemplatePrefix: "Users/"),
        new("Image", "GetItemImage", ["itemId"]),
    ];

    /// <summary>
    /// Finds requirements the running server's actions don't meet.
    /// </summary>
    /// <param name="actions">The server's actions.</param>
    /// <returns>One line per unmet requirement.</returns>
    public static IReadOnlyList<string> FindMissing(IEnumerable<ActionShape> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var byName = actions.ToLookup(a => (a.Controller, a.Action));
        var missing = new List<string>();
        foreach (var r in Requirements)
        {
            var name = $"{r.Controller}.{r.Action}";
            var candidates = byName[(r.Controller, r.Action)].ToArray();
            if (candidates.Length == 0)
            {
                missing.Add($"{name}: action not found");
                continue;
            }

            // Jellyfin may route one action several ways (GET and HEAD); every one must match.
            foreach (var action in candidates)
            {
                var names = action.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var absent = r.Parameters.Where(p => !names.Contains(p)).ToArray();
                if (absent.Length > 0)
                {
                    missing.Add($"{name}: parameter(s) {string.Join(", ", absent)} not found");
                }

                foreach (var property in r.BodyProperties ?? [])
                {
                    if (!action.Parameters.Any(p => p.Type.GetProperty(property) is not null))
                    {
                        missing.Add($"{name}: no parameter type has property {property}");
                    }
                }

                if (r.Returns is { } returns && (action.ReturnType is null || !Mentions(action.ReturnType, returns)))
                {
                    missing.Add($"{name}: return type no longer contains {returns.Name}");
                }

                if (r.TemplatePrefix is { } prefix
                    && !(action.Template?.TrimStart('/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    missing.Add($"{name}: route no longer starts with {prefix}");
                }
            }
        }

        return missing.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Finds model members the scrubber and filter use that the loaded Jellyfin assemblies lack.
    /// </summary>
    /// <returns>One line per missing member.</returns>
    public static IReadOnlyList<string> FindMissingModelMembers()
    {
        (Type Type, string[] Members)[] members =
        [
            (typeof(MediaSourceInfo), ["Path", "EncoderPath", "RequiredHttpHeaders", "OpenToken", "LiveStreamId", "Id"]),
            (typeof(PlaybackInfoResponse), ["MediaSources"]),
            (typeof(LiveStreamResponse), ["MediaSource"]),
            (typeof(BaseItemDto), ["MediaSources", "Type"]),
            (typeof(SessionInfoDto), ["NowPlayingItem", "NowPlayingQueueFullItems"]),
            (typeof(QueryResult<BaseItemDto>), ["Items"]),
            (typeof(LiveTvOptions), ["TunerHosts", "ListingProviders"]),
            (typeof(TunerHostInfo), ["Id", "Type", "Url", "Source"]),
            (typeof(ListingsProviderInfo), ["Path", "Username", "Password", "ListingsId"]),
            (typeof(ChannelInfo), ["Id", "Path"]),
            (typeof(DtoOptions), ["Fields"]),
        ];
        return members
            .SelectMany(m => m.Members.Where(name => m.Type.GetProperty(name) is null).Select(name => $"{m.Type.Name}.{name} not found"))
            .ToArray();
    }

    private static bool Mentions(Type type, Type target)
    {
        if (target.IsAssignableFrom(type))
        {
            return true;
        }

        if (type.IsArray)
        {
            return Mentions(type.GetElementType()!, target);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(t => Mentions(t, target));
    }
}
