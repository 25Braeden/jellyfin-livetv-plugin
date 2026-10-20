using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveTvAccess.Core;

/// <summary>
/// Checks the plugin's tuner configuration against Jellyfin's M3U tuners and the channels in the library.
/// </summary>
public static class TunerHealth
{
    /// <summary>
    /// Evaluates the tuner configuration.
    /// </summary>
    /// <param name="catalog">The configured tuners (valid entries).</param>
    /// <param name="configuredEntries">How many tuner entries the configuration has (invalid ones included).</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="jellyfinM3uUrls">The URLs of Jellyfin's M3U tuners.</param>
    /// <param name="channelExternalIds">The ExternalId of every Live TV channel in the library.</param>
    /// <returns>The result.</returns>
    public static TunerHealthResult Evaluate(
        TunerCatalog catalog,
        int configuredEntries,
        UserTunerMap map,
        IReadOnlyCollection<string?> jellyfinM3uUrls,
        IEnumerable<string?> channelExternalIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(jellyfinM3uUrls);
        ArgumentNullException.ThrowIfNull(channelExternalIds);

        var problems = new List<string>();
        var warnings = new List<string>();
        var keys = catalog.Keys;
        if (keys.Count == 0)
        {
            problems.Add("No tuners are configured in the LiveTvAccess plugin: Live TV is denied for every non-admin.");
        }

        if (configuredEntries > keys.Count)
        {
            warnings.Add($"{configuredEntries - keys.Count} tuner entr{(configuredEntries - keys.Count == 1 ? "y was" : "ies were")} ignored (invalid or reserved key, missing URL, or duplicate); see the server log.");
        }

        // Count channels per tuner, and which tuners list each stream URL (by media source id, md5 of the URL).
        var counts = keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        var sources = new Dictionary<string, (SortedSet<string> Tuners, int Channels)>(StringComparer.OrdinalIgnoreCase);
        foreach (var externalId in channelExternalIds)
        {
            var key = catalog.ResolveChannel(externalId);
            if (key is null)
            {
                continue;
            }

            counts[key]++;
            if (JellyfinIds.M3uMediaSourceId(externalId) is { } source)
            {
                var entry = sources.TryGetValue(source, out var e) ? e : (Tuners: new SortedSet<string>(StringComparer.Ordinal), Channels: 0);
                entry.Tuners.Add(key);
                sources[source] = (entry.Tuners, entry.Channels + 1);
            }
        }

        var jellyfinUrls = new HashSet<string>(jellyfinM3uUrls.Where(u => !string.IsNullOrEmpty(u))!, StringComparer.Ordinal);
        var tuners = new List<TunerStatus>();
        foreach (var key in keys)
        {
            var url = catalog.UrlOf(key)!;
            var inJellyfin = jellyfinUrls.Contains(url);
            var users = map.UserCountOf(key);
            tuners.Add(new TunerStatus(key, Redact.Location(url), Redact.ShortHash(url), inJellyfin, counts[key], users));
            if (!inJellyfin)
            {
                problems.Add($"Tuner '{key}': its URL is not a Jellyfin M3U tuner (the URL must match a Live TV tuner exactly).");
            }
            else if (counts[key] == 0)
            {
                problems.Add($"Tuner '{key}': no channels in the library (wrong URL, empty playlist, or Refresh Guide hasn't run).");
            }

            if (users == 0)
            {
                warnings.Add($"Tuner '{key}': no users are assigned to it.");
            }
        }

        var unconfigured = jellyfinUrls.Count(u => catalog.ResolveUrl(u) is null);
        if (unconfigured > 0)
        {
            warnings.Add($"{unconfigured} Jellyfin M3U tuner(s) are not configured in this plugin; their channels are hidden from every non-admin.");
        }

        var duplicates = sources
            .Where(s => s.Value.Tuners.Count > 1)
            .Select(s => new DuplicateStreamUrl(s.Key, Redact.ShortHash(s.Key), s.Value.Tuners.ToArray(), s.Value.Channels))
            .OrderBy(d => d.SourceHash, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            warnings.Add($"{duplicates.Length} stream URL(s) are listed by more than one tuner (the same stream URL on two tuners): users of different tuners block each other on those channels. See Duplicates.");
        }

        return new TunerHealthResult(tuners, problems, warnings, duplicates);
    }
}
