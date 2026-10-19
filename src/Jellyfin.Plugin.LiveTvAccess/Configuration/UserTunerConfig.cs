namespace Jellyfin.Plugin.LiveTvAccess.Configuration;

/// <summary>
/// One user-to-tuner assignment in the plugin configuration.
/// </summary>
public class UserTunerConfig
{
    /// <summary>
    /// Gets or sets the Jellyfin username.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tuner key the user may use.
    /// </summary>
    public string TunerKey { get; set; } = string.Empty;
}
