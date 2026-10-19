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
    /// Gets the open-token / live-stream-id prefix of the Live TV provider ("e2329f49..." on 10.11.11).
    /// </summary>
    public static string LiveTvProviderPrefix { get; } = Md5Guid(LiveTvProviderTypeName);

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
