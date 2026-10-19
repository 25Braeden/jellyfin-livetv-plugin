using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using LiveTvAccess.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Jellyfin-side lookups for the access rule: who is calling, and which tuner a channel or stream is on.
/// The rule itself lives in <see cref="AccessPolicy"/> (Core).
/// </summary>
public class AccessService
{
    // Claim names set by Jellyfin.Api/Auth/CustomAuthenticationHandler.cs:62-70
    // (constants in Jellyfin.Api/Constants/InternalClaimTypes.cs, not shipped on NuGet). Version-fragile.
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<AccessService> _logger;
    private readonly object _lock = new();
    private (TunerCatalog Catalog, UserTunerMap Map)? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessService"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="configurationManager">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AccessService}"/> interface.</param>
    public AccessService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        IConfigurationManager configurationManager,
        ILogger<AccessService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the tuner catalog from the current configuration.
    /// </summary>
    public TunerCatalog Catalog => GetState().Catalog;

    /// <summary>
    /// Gets the user-to-tuner map from the current configuration.
    /// </summary>
    public UserTunerMap Map => GetState().Map;

    /// <summary>
    /// Drops the cached map so the next call rebuilds it (on config change, user added/renamed).
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _state = null;
        }
    }

    /// <summary>
    /// Gets the caller from the request principal. Returns <c>null</c> for anonymous requests.
    /// </summary>
    /// <param name="principal">The request principal.</param>
    /// <returns>The caller, or <c>null</c>.</returns>
    public Caller? GetCaller(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var isApiKey = bool.TryParse(principal.FindFirstValue(IsApiKeyClaim), out var k) && k;
        var hasUserId = Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId);
        if (!isApiKey && (!hasUserId || userId == Guid.Empty))
        {
            return null;
        }

        var user = hasUserId && userId != Guid.Empty ? _userManager.GetUserById(userId) : null;
        var isAdmin = user is not null && user.HasPermission(PermissionKind.IsAdministrator);
        return new Caller(hasUserId ? userId : Guid.Empty, isApiKey, isAdmin);
    }

    /// <summary>
    /// Gets the tuner a library item belongs to, if it is a Live TV channel or program.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="isLiveTv"><c>true</c> if the item is a Live TV channel or program.</param>
    /// <returns>The tuner key, or <c>null</c> if not Live TV or unresolvable.</returns>
    public string? GetItemTuner(Guid itemId, out bool isLiveTv)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);
        if (item is LiveTvProgram program)
        {
            isLiveTv = true;
            return (_libraryManager.GetItemById(program.ChannelId) as LiveTvChannel) is { } parent
                ? Catalog.ResolveChannel(parent.ExternalId)
                : null;
        }

        isLiveTv = item is LiveTvChannel;
        return item is LiveTvChannel channel ? Catalog.ResolveChannel(channel.ExternalId) : null;
    }

    /// <summary>
    /// Gets the tuner an open live stream is using.
    /// </summary>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="isLiveTv"><c>true</c> if the id belongs to the Live TV provider.</param>
    /// <returns>The tuner key, or <c>null</c> if not Live TV, not open, or unresolvable.</returns>
    public string? GetLiveStreamTuner(string liveStreamId, out bool isLiveTv)
    {
        isLiveTv = liveStreamId.StartsWith(JellyfinIds.LiveTvProviderPrefix + "_", StringComparison.OrdinalIgnoreCase);
        if (!isLiveTv)
        {
            return null;
        }

        var tunerHostId = _mediaSourceManager.GetLiveStreamInfo(liveStreamId)?.TunerHostId;
        if (string.IsNullOrEmpty(tunerHostId))
        {
            return null;
        }

        var url = _configurationManager.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
            .FirstOrDefault(h => string.Equals(h.Id, tunerHostId, StringComparison.OrdinalIgnoreCase))?.Url;
        return Catalog.ResolveUrl(url);
    }

    private (TunerCatalog Catalog, UserTunerMap Map) GetState()
    {
        lock (_lock)
        {
            return _state ??= Build(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        }
    }

    private (TunerCatalog Catalog, UserTunerMap Map) Build(PluginConfiguration config)
    {
        TunerCatalog catalog;
        try
        {
            var tuners = (config.Tuners ?? []).Where(t => t is not null);
            catalog = new TunerCatalog(tuners.Select(t => new KeyValuePair<string, string>(t.Key, t.Url)));
        }
        catch (ArgumentException ex)
        {
            // Fail closed: invalid tuner definitions mean nobody (except admins / API keys) gets a tuner.
            _logger.LogError(ex, "LiveTvAccess: invalid tuner configuration; all non-admin Live TV access is blocked");
            return (new TunerCatalog([]), new UserTunerMap([], []));
        }

        // Users are matched by id only (resolved from names when the config was saved). Bad entries are
        // skipped one by one and never affect other users.
        var entries = new List<UserTunerEntry?>();
        foreach (var e in config.Users ?? [])
        {
            if (e is not null && e.UserId != Guid.Empty && _userManager.GetUserById(e.UserId) is null)
            {
                _logger.LogWarning("LiveTvAccess: configured user id {UserId} ('{UserName}') no longer exists; skipped", e.UserId, e.UserName);
                continue;
            }

            entries.Add(e is null ? null : new UserTunerEntry(e.UserId, e.TunerKey, e.UserName));
        }

        var map = UserTunerMap.FromEntries(catalog.Keys, entries, w => _logger.LogWarning("LiveTvAccess: {Warning}", w));
        _logger.LogInformation(
            "LiveTvAccess: config loaded, tuners [{Tuners}], {Count} user assignment(s)",
            string.Join(", ", catalog.Keys),
            map.AssignedUserCount);
        return (catalog, map);
    }
}
