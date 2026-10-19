using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveTvAccess.Configuration;

/// <summary>
/// Plugin configuration: the tuners and which user gets which tuner.
/// Users not listed get no tuner. Admins and API keys are never restricted.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML/JSON config serialization, same as Jellyfin's own options.")]
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the tuners: a short key (used in the "livetv-{key}" tag) and the M3U URL exactly as
    /// configured in Jellyfin's Live TV tuner settings.
    /// </summary>
    public TunerConfig[] Tuners { get; set; } = Array.Empty<TunerConfig>();

    /// <summary>
    /// Gets or sets the user-to-tuner assignments, by username.
    /// </summary>
    public UserTunerConfig[] Users { get; set; } = Array.Empty<UserTunerConfig>();

    /// <summary>
    /// Gets or sets every "livetv-*" tag the plugin has applied (maintained by the plugin). Lets it clean up a
    /// removed tuner's tag while never touching "livetv-*" tags it didn't create. Preserved across config saves.
    /// </summary>
    public string[] OwnedTagHistory { get; set; } = Array.Empty<string>();
}
