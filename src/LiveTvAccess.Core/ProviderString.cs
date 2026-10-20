using System;

namespace LiveTvAccess.Core;

/// <summary>
/// What a "{32 hex}_..." string (a media source provider prefix) is.
/// </summary>
public enum ProviderStringKind
{
    /// <summary>Not provider-prefixed.</summary>
    None,

    /// <summary>A Live TV open token: <c>{liveTvPrefix}_{Type}_{itemIdN}[_{sourceId}]</c>.</summary>
    LiveTvOpenToken,

    /// <summary>A Live TV live stream id: <c>{liveTvPrefix}_{serviceHash}_{sourceId}</c>.</summary>
    LiveTvStreamId,

    /// <summary>Prefixed by some other provider: a token or stream id we can only resolve if the stream is open.</summary>
    OtherProvider,
}

/// <summary>
/// Classifies provider-prefixed strings (open tokens, live stream ids) by shape. Version-fragile: mirrors
/// Emby.Server.Implementations/Library/MediaSourceManager.cs:321-334 and LiveTvMediaSourceProvider.cs:108-140.
/// </summary>
public static class ProviderString
{
    /// <summary>
    /// Classifies a string.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <param name="itemId">For <see cref="ProviderStringKind.LiveTvOpenToken"/>, the channel item id if parseable
    /// (any GUID format, like Jellyfin's <c>new Guid(string)</c>); otherwise <see cref="Guid.Empty"/>.</param>
    /// <returns>The kind.</returns>
    public static ProviderStringKind Classify(string? value, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (value is null)
        {
            return ProviderStringKind.None;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 34 || trimmed[32] != '_' || !IsHex(trimmed.AsSpan(0, 32)))
        {
            return ProviderStringKind.None;
        }

        // Jellyfin compares the prefix exactly (case-insensitive) after splitting on '_'
        // (MediaSourceManager.cs:886-899), so a padded value isn't a Live TV token: treat as another provider.
        if (trimmed.Length != value.Length
            || !trimmed.StartsWith(JellyfinIds.LiveTvProviderPrefix + "_", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderStringKind.OtherProvider;
        }

        // After the prefix: "{serviceHash}_{sourceId}" (stream id) or "{Type}_{itemId}[_{sourceId}]" (open token).
        var keys = trimmed[33..].Split('_', 3);
        if (keys[0].Length == 32 && IsHex(keys[0]))
        {
            return ProviderStringKind.LiveTvStreamId;
        }

        if (keys.Length >= 2 && TryParseGuid(keys[1], out var id) && id != Guid.Empty)
        {
            itemId = id;
        }

        return ProviderStringKind.LiveTvOpenToken;
    }

    /// <summary>
    /// Splits a Live TV open token like Jellyfin does (LiveTvMediaSourceProvider.cs:131-136):
    /// <c>{prefix}_{Type}_{channelId}[_{sourceId}]</c>.
    /// </summary>
    /// <param name="value">The token.</param>
    /// <param name="channelIdText">The channel id part as sent.</param>
    /// <param name="sourceId">The media source part, or <c>null</c> if absent.</param>
    /// <returns><c>false</c> if it isn't a Live TV open token.</returns>
    public static bool TrySplitLiveTvOpenToken(string? value, out string channelIdText, out string? sourceId)
    {
        channelIdText = string.Empty;
        sourceId = null;
        if (Classify(value, out _) != ProviderStringKind.LiveTvOpenToken)
        {
            return false;
        }

        var keys = value!.Trim()[33..].Split('_', 3);
        if (keys.Length < 2)
        {
            return false;
        }

        channelIdText = keys[1];
        sourceId = keys.Length >= 3 ? keys[2] : null;
        return true;
    }

    /// <summary>
    /// Gets whether a string is a GUID in any format Jellyfin's <c>new Guid(string)</c> accepts.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <param name="guid">The GUID.</param>
    /// <returns><c>true</c> if it is.</returns>
    public static bool TryParseGuid(string? value, out Guid guid)
    {
        return Guid.TryParse(value, out guid);
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
