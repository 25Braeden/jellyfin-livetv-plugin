using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveTvAccess.Core;

/// <summary>
/// Which "livetv-*" tags the plugin owns, and how to change a tag list without touching anyone else's tags.
/// The plugin owns exactly: the tag of every configured tuner, <see cref="TunerTags.Unassigned"/>, and every tag it
/// applied before (history, so a removed tuner's tag is still cleaned up). Any other "livetv-*" tag is foreign:
/// kept as is and reported.
/// </summary>
public static class TagOwnership
{
    /// <summary>
    /// Gets the set of tags the plugin owns (case-insensitive).
    /// </summary>
    /// <param name="tunerKeys">The configured tuner keys.</param>
    /// <param name="history">Tags the plugin applied before (non-"livetv-" entries are ignored).</param>
    /// <returns>The owned tags.</returns>
    public static IReadOnlySet<string> Owned(IEnumerable<string> tunerKeys, IEnumerable<string> history)
    {
        ArgumentNullException.ThrowIfNull(tunerKeys);
        ArgumentNullException.ThrowIfNull(history);

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TunerTags.Unassigned };
        owned.UnionWith(tunerKeys.Select(TunerTags.ForTuner));
        owned.UnionWith(history.Where(TunerTags.IsManaged).Select(h => h.Trim().ToLowerInvariant()));
        return owned;
    }

    /// <summary>
    /// Replaces the owned tags in <paramref name="existing"/> with <paramref name="desired"/>, keeping every tag the
    /// plugin doesn't own.
    /// </summary>
    /// <param name="existing">The current tag list.</param>
    /// <param name="owned">The owned tags (from <see cref="Owned"/>).</param>
    /// <param name="desired">The owned tags this list should have.</param>
    /// <param name="foreign">"livetv-*" tags in <paramref name="existing"/> the plugin doesn't own (kept).</param>
    /// <returns>The new tag list.</returns>
    public static IReadOnlyList<string> Merge(
        IEnumerable<string> existing,
        IReadOnlySet<string> owned,
        IEnumerable<string> desired,
        out IReadOnlyList<string> foreign)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(desired);

        var kept = existing.Where(t => !owned.Contains(t)).ToList();
        foreign = kept.Where(TunerTags.IsManaged).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return kept.Concat(desired).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
