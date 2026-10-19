namespace Jellyfin.Plugin.LiveTvAccess.Configuration;

/// <summary>
/// One tuner in the plugin configuration.
/// </summary>
public class TunerConfig
{
    /// <summary>
    /// Gets or sets the short tuner key, e.g. "tuner1" (a-z, 0-9, '-').
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tuner's M3U URL or path exactly as configured in Jellyfin, e.g. "/tuners/tuner1.m3u".
    /// </summary>
    public string Url { get; set; } = string.Empty;
}
