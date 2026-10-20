using System;
using System.Security.Cryptography;
using System.Text;

namespace LiveTvAccess.Core;

/// <summary>
/// Reproduces the ID schemes Jellyfin 10.11.11 uses for M3U channels and live-stream open tokens.
/// Pure string logic (no Jellyfin reference) so a proxy fallback can use it too. Version-fragile.
/// </summary>
public static class JellyfinIds
{
    /// <summary>
    /// Full type name of Jellyfin's Live TV media source provider. Open tokens and live stream IDs are
    /// prefixed with <c>Md5Guid(this)</c> (Emby.Server.Implementations/Library/MediaSourceManager.cs:321-334).
    /// </summary>
    public const string LiveTvProviderTypeName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";

    /// <summary>
    /// Full type name of Jellyfin's default Live TV service; live stream ids contain <c>Md5Guid(this)</c>
    /// (src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs:301-303).
    /// </summary>
    public const string LiveTvServiceTypeName = "Jellyfin.LiveTv.DefaultLiveTvService";

    /// <summary>
    /// Gets the open-token / live-stream-id prefix of the Live TV provider ("e2329f49..." on 10.11.11).
    /// </summary>
    public static string LiveTvProviderPrefix { get; } = Md5Guid(LiveTvProviderTypeName);

    /// <summary>
    /// Gets the live stream id prefix of the default Live TV service ("af999c25..." on 10.11.11).
    /// </summary>
    public static string LiveTvServicePrefix { get; } = Md5Guid(LiveTvServiceTypeName);

    /// <summary>
    /// Gets the media source id of an M3U channel from its ExternalId: the md5 of its stream URL, which is the last 32
    /// characters of <c>"m3u_" + md5(tunerUrl) + md5(streamUrl)</c> (M3uParser.cs:104, M3UTunerHost.cs:195).
    /// </summary>
    /// <param name="externalId">The channel ExternalId.</param>
    /// <returns>The media source id, or <c>null</c> if the id isn't in that form.</returns>
    public static string? M3uMediaSourceId(string? externalId)
    {
        const int Length = 4 + 32 + 32;
        if (externalId is null || externalId.Length != Length || !externalId.StartsWith("m3u_", StringComparison.Ordinal))
        {
            return null;
        }

        var source = externalId[^32..];
        foreach (var c in source)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return source;
    }

    /// <summary>
    /// Gets the live stream id Jellyfin gives a Live TV stream with this media source id:
    /// <c>{provider}_{service}_{sourceId}</c> (MediaSourceManager.cs:321-334, LiveTvMediaSourceProvider.cs:301-303).
    /// </summary>
    /// <param name="mediaSourceId">The media source id (md5 of the stream URL).</param>
    /// <returns>The live stream id.</returns>
    public static string LiveTvStreamId(string mediaSourceId)
    {
        ArgumentNullException.ThrowIfNull(mediaSourceId);
        return LiveTvProviderPrefix + "_" + LiveTvServicePrefix + "_" + mediaSourceId;
    }

    /// <summary>
    /// Gets whether an open token's media source part is allowed for its channel: empty, the token's own channel id
    /// text, or the channel's own media source id. Anything else would let the token pick another stream to share
    /// (DefaultLiveTvService.cs:466-476 matches open streams by this value only).
    /// </summary>
    /// <param name="sourceId">The token's third part, or <c>null</c>.</param>
    /// <param name="channelIdText">The token's second part as sent.</param>
    /// <param name="channelMediaSourceId">The channel's own media source id.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public static bool IsAllowedOpenTokenSource(string? sourceId, string channelIdText, string? channelMediaSourceId)
    {
        // Jellyfin treats a source equal to the channel id as "no source" (LiveTvMediaSourceProvider.cs:265-268).
        return string.IsNullOrEmpty(sourceId)
            || string.Equals(sourceId, channelIdText, StringComparison.OrdinalIgnoreCase)
            || (channelMediaSourceId is not null && string.Equals(sourceId, channelMediaSourceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Jellyfin's <c>string.GetMD5().ToString("N")</c>: MD5 over UTF-16LE, read as a .NET Guid
    /// (MediaBrowser.Common/Extensions/BaseExtensions.cs:30-35).
    /// </summary>
    /// <param name="value">The input string.</param>
    /// <returns>32 lowercase hex characters.</returns>
    public static string Md5Guid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
#pragma warning disable CA5351 // Not security: reproducing Jellyfin's ID scheme.
        return new Guid(MD5.HashData(Encoding.Unicode.GetBytes(value))).ToString("N");
#pragma warning restore CA5351
    }

    /// <summary>
    /// Gets the channel ExternalId prefix for an M3U tuner: <c>"m3u_" + Md5Guid(url)</c>
    /// (src/Jellyfin.LiveTv/TunerHosts/M3UTunerHost.cs:64-67, M3uParser.cs:104).
    /// </summary>
    /// <param name="tunerUrl">The tuner's M3U URL or path exactly as configured in Jellyfin.</param>
    /// <returns>The prefix.</returns>
    public static string M3uChannelPrefix(string tunerUrl)
    {
        return "m3u_" + Md5Guid(tunerUrl);
    }

    /// <summary>
    /// Parses the channel item id out of a Live TV open token:
    /// <c>{providerPrefix}_LiveTvChannel_{itemIdN}[_{mediaSourceId}]</c>
    /// (src/Jellyfin.LiveTv/LiveTvMediaSourceProvider.cs:108-115 and 131-136).
    /// </summary>
    /// <param name="openToken">The open token.</param>
    /// <param name="isLiveTvToken"><c>true</c> if the token belongs to the Live TV provider at all.</param>
    /// <param name="itemId">The channel item id, when parseable.</param>
    /// <returns><c>true</c> if a Live TV token with a valid item id.</returns>
    public static bool TryParseLiveTvOpenToken(string? openToken, out bool isLiveTvToken, out Guid itemId)
    {
        itemId = Guid.Empty;
        isLiveTvToken = openToken is not null
            && openToken.StartsWith(LiveTvProviderPrefix + "_", StringComparison.OrdinalIgnoreCase);
        if (!isLiveTvToken)
        {
            return false;
        }

        // Same split as Jellyfin: provider prefix, then "{type}_{itemId}_{sourceId}" split into at most 3.
        var keys = openToken![(LiveTvProviderPrefix.Length + 1)..].Split('_', 3);
        return keys.Length >= 2 && Guid.TryParseExact(keys[1], "N", out itemId) && itemId != Guid.Empty;
    }
}
