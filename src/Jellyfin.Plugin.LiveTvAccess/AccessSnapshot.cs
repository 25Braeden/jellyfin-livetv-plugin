using System.Collections.Generic;
using LiveTvAccess.Core;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// One immutable view of the configuration. Each access decision and each sync pass takes exactly one snapshot,
/// so a config change can never mix an old catalog with a new user map.
/// </summary>
/// <param name="Catalog">The tuners.</param>
/// <param name="Map">The user-to-tuner map.</param>
/// <param name="OwnedTags">Tags the plugin owns (configured tuners, "unassigned", and history).</param>
/// <param name="NewlyClaimedTags">Owned tags this configuration claimed for the first time.</param>
/// <param name="Version">Increments on every invalidation; a sync pass stops if it changes.</param>
public sealed record AccessSnapshot(
    TunerCatalog Catalog,
    UserTunerMap Map,
    IReadOnlySet<string> OwnedTags,
    IReadOnlySet<string> NewlyClaimedTags,
    long Version);
