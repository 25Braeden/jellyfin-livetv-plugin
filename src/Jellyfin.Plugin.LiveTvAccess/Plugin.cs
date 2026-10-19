using System;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Per-user Live TV access plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// Created by Jellyfin through DI (Emby.Server.Implementations/Plugins/PluginManager.cs:554).
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        logger.LogInformation("LiveTvAccess plugin {Version} loaded", Version);
    }

    /// <inheritdoc />
    public override string Name => "LiveTvAccess";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4d5aa36c-9164-4f4d-ba7f-0fa6c25bfd28");

    /// <inheritdoc />
    public override string Description => "Per-user Live TV tuner access.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }
}
