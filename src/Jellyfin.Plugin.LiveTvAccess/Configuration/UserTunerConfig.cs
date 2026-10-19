using System;

namespace Jellyfin.Plugin.LiveTvAccess.Configuration;

/// <summary>
/// One user-to-tuner assignment in the plugin configuration.
/// Access is granted by <see cref="UserId"/> only. <see cref="UserName"/> is input/display: when the
/// configuration is saved without an id, the plugin resolves the name to an id once and stores it.
/// </summary>
public class UserTunerConfig
{
    /// <summary>
    /// Gets or sets the Jellyfin user id. This is what grants access.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin username (resolved to <see cref="UserId"/> on save; refreshed for display).
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tuner key the user may use.
    /// </summary>
    public string TunerKey { get; set; } = string.Empty;
}
