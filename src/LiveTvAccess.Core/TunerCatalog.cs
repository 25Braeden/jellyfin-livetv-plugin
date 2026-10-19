using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveTvAccess.Core;

/// <summary>
/// The configured tuners (key + M3U URL) and how to tell which one a channel or stream belongs to.
/// </summary>
public sealed class TunerCatalog
{
    private readonly (string Key, string Url, string Prefix)[] _tuners;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerCatalog"/> class.
    /// </summary>
    /// <param name="tuners">Tuner key to M3U URL (as configured in Jellyfin).</param>
    public TunerCatalog(IEnumerable<KeyValuePair<string, string>> tuners)
    {
        ArgumentNullException.ThrowIfNull(tuners);

        _tuners = tuners
            .Select(t => (Key: TunerTags.NormalizeKey(t.Key), Url: t.Value, Prefix: JellyfinIds.M3uChannelPrefix(t.Value)))
            .ToArray();

        var duplicate = _tuners.GroupBy(t => t.Key).FirstOrDefault(g => g.Count() > 1)
            ?? _tuners.GroupBy(t => t.Url, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate tuner '{duplicate.Key}'.", nameof(tuners));
        }
    }

    /// <summary>
    /// Gets the tuner keys.
    /// </summary>
    public IReadOnlyList<string> Keys => _tuners.Select(t => t.Key).ToArray();

    /// <summary>
    /// Gets the tuner key for a channel from its Jellyfin ExternalId.
    /// </summary>
    /// <param name="channelExternalId">The LiveTvChannel ExternalId (e.g. "m3u_{md5(url)}{md5(stream)}").</param>
    /// <returns>The tuner key, or <c>null</c> if no configured tuner matches.</returns>
    public string? ResolveChannel(string? channelExternalId)
    {
        if (string.IsNullOrEmpty(channelExternalId))
        {
            return null;
        }

        var matches = _tuners.Where(t => channelExternalId.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Key : null;
    }

    /// <summary>
    /// Gets the tuner key for a tuner URL.
    /// </summary>
    /// <param name="tunerUrl">The tuner URL as configured in Jellyfin.</param>
    /// <returns>The tuner key, or <c>null</c> if not configured.</returns>
    public string? ResolveUrl(string? tunerUrl)
    {
        return _tuners.FirstOrDefault(t => string.Equals(t.Url, tunerUrl, StringComparison.Ordinal)).Key;
    }
}
