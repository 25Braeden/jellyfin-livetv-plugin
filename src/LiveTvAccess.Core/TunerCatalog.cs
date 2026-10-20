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
    /// Gets a tuner's URL (as configured).
    /// </summary>
    /// <param name="key">The tuner key.</param>
    /// <returns>The URL, or <c>null</c> for an unknown key.</returns>
    public string? UrlOf(string key) => _tuners.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal)).Url;

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

    /// <summary>
    /// Builds a catalog from configured tuners, skipping (with a warning) any entry that is null, has an invalid or
    /// reserved key, or has no URL. Keys or URLs that appear more than once are ambiguous: every entry involved is
    /// skipped. A bad entry never affects the other tuners.
    /// </summary>
    /// <param name="entries">The configured tuners (may contain nulls).</param>
    /// <param name="warn">Receives one message per skipped entry.</param>
    /// <returns>The catalog.</returns>
    public static TunerCatalog FromEntries(IEnumerable<TunerEntry?> entries, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(warn);

        var valid = new List<(string Key, string Url, int Index)>();
        var index = 0;
        foreach (var entry in entries)
        {
            index++;
            if (entry is null)
            {
                warn($"tuner entry #{index} is empty; skipped");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                warn($"tuner entry #{index} ('{entry.Key}') has no URL; skipped");
                continue;
            }

            try
            {
                valid.Add((TunerTags.NormalizeKey(entry.Key!), entry.Url, index));
            }
            catch (ArgumentException ex)
            {
                warn($"tuner entry #{index}: {ex.Message.Split(" (Parameter", 2)[0]} Skipped");
            }
        }

        // Ambiguous: a key or URL used by more than one entry. Skip every entry involved.
        var dupKeys = valid.GroupBy(t => t.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var dupUrls = valid.GroupBy(t => t.Url, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var kept = new List<KeyValuePair<string, string>>();
        foreach (var (key, url, i) in valid)
        {
            if (dupKeys.Contains(key) || dupUrls.Contains(url))
            {
                warn($"tuner entry #{i} ('{key}', {url}) duplicates another entry's key or URL; skipped");
                continue;
            }

            kept.Add(new KeyValuePair<string, string>(key, url));
        }

        return new TunerCatalog(kept);
    }
}
