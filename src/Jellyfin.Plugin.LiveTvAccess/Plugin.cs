using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Per-user Live TV access plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// Created by Jellyfin through DI (Emby.Server.Implementations/Plugins/PluginManager.cs:554).
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger, IUserManager userManager)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        _userManager = userManager;
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

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }

    /// <summary>
    /// Saves a new configuration (Jellyfin's plugin config API calls this). Usernames are resolved to user ids
    /// here, once, so later renames can't move a tuner to another account; access is granted by id only.
    /// </summary>
    /// <param name="configuration">The new configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            ResolveUserIds(config);

            // The owned-tag history is the plugin's own bookkeeping: keep it even if the saved config omits it.
            config.OwnedTagHistory = (Configuration?.OwnedTagHistory ?? [])
                .Concat(config.OwnedTagHistory ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        base.UpdateConfiguration(configuration);
    }

    private void ResolveUserIds(PluginConfiguration config)
    {
        config.Users ??= [];
        config.Tuners ??= [];
        var dropped = new List<UserTunerConfig>();
        foreach (var entry in config.Users)
        {
            if (entry is null)
            {
                continue;
            }

            if (entry.UserId != Guid.Empty)
            {
                // Id given: it wins. Refresh the display name if the user still exists.
                var byId = _userManager.GetUserById(entry.UserId);
                if (byId is null)
                {
                    _logger.LogWarning("LiveTvAccess: configured user id {UserId} does not exist", entry.UserId);
                }
                else
                {
                    entry.UserName = byId.Username;
                }

                continue;
            }

            var byName = string.IsNullOrWhiteSpace(entry.UserName) ? null : _userManager.GetUserByName(entry.UserName);
            if (byName is null)
            {
                // Don't keep it: a later save would resolve the name to whoever has renamed themselves to it
                // (non-admins can rename themselves, UserController.cs:388-395).
                _logger.LogWarning("LiveTvAccess: configured user '{UserName}' does not exist; entry dropped", entry.UserName);
                dropped.Add(entry);
                continue;
            }

            entry.UserId = byName.Id;
            entry.UserName = byName.Username;
        }

        if (dropped.Count > 0)
        {
            config.Users = config.Users.Where(e => !dropped.Contains(e)).ToArray();
        }
    }
}
