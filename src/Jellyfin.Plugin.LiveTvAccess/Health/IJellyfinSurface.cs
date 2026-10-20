using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// The parts of the running Jellyfin server the filter depends on, read for the self-check.
/// </summary>
public interface IJellyfinSurface
{
    /// <summary>
    /// Gets whether the plugin's action filter is registered globally.
    /// </summary>
    /// <returns><c>true</c> if registered.</returns>
    bool IsFilterRegistered();

    /// <summary>
    /// Gets every controller action.
    /// </summary>
    /// <returns>The actions.</returns>
    IReadOnlyList<ActionShape> GetActions();

    /// <summary>
    /// Gets Jellyfin's claim type names for the user id and the API-key flag, or <c>null</c> if not found.
    /// </summary>
    /// <returns>The claim type names.</returns>
    (string UserId, string IsApiKey)? GetClaimTypes();
}
