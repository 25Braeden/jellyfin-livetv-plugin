using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
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
    private AccessSnapshot? _snapshot;
    private long _version;
    private long _lastUnrecognizedWarning = long.MinValue / 2;

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
    /// Gets the current immutable snapshot of the configuration. Take it once per decision or sync pass.
    /// </summary>
    public AccessSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot ??= Build(Plugin.Instance?.Configuration ?? new PluginConfiguration(), _version);
            }
        }
    }

    /// <summary>
    /// Gets the current configuration version (increments on every invalidation).
    /// </summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next read rebuilds it (config change, user deleted).
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _version++;
            _snapshot = null;
        }
    }

    /// <summary>
    /// Gets the caller from the request principal: <c>null</c> for anonymous requests (Jellyfin's own authorization
    /// decides those). An authenticated principal without a usable user claim, or whose user no longer exists,
    /// becomes a caller with no Live TV access, so it is denied rather than treated as anonymous.
    /// </summary>
    /// <param name="principal">The request principal.</param>
    /// <returns>The caller, or <c>null</c> for anonymous requests.</returns>
    public Caller? GetCaller(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var isApiKey = bool.TryParse(principal.FindFirstValue(IsApiKeyClaim), out var k) && k;
        var hasUserId = Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId) && userId != Guid.Empty;
        if (hasUserId)
        {
            var user = _userManager.GetUserById(userId);
            return new Caller(
                userId,
                isApiKey,
                IsAdministrator: user is not null && user.HasPermission(PermissionKind.IsAdministrator),
                HasLiveTvAccess: user is not null && user.HasPermission(PermissionKind.EnableLiveTvAccess));
        }

        if (isApiKey)
        {
            // Jellyfin API keys carry no user (Jellyfin.Server.Implementations/Security/AuthorizationContext.cs:195-217).
            return new Caller(Guid.Empty, IsApiKey: true, IsAdministrator: false, HasLiveTvAccess: false);
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Authenticated, but neither a user nor an API key we recognize: Jellyfin's claim contract may have changed.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastUnrecognizedWarning) > (long)TimeSpan.FromMinutes(5).TotalMilliseconds)
        {
            Interlocked.Exchange(ref _lastUnrecognizedWarning, now);
            _logger.LogWarning("LiveTvAccess: authenticated request without a recognizable user claim; denying Live TV access (has Jellyfin's claim format changed?)");
        }

        return new Caller(Guid.Empty, IsApiKey: false, IsAdministrator: false, HasLiveTvAccess: false);
    }

    /// <summary>
    /// Gets the tuner a library item belongs to, if it is a Live TV channel or program.
    /// </summary>
    /// <param name="snapshot">The snapshot this decision uses.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="isLiveTv"><c>true</c> if the item is a Live TV channel or program.</param>
    /// <returns>The tuner key, or <c>null</c> if not Live TV or unresolvable.</returns>
    public string? GetItemTuner(AccessSnapshot snapshot, Guid itemId, out bool isLiveTv)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);
        if (item is LiveTvProgram program)
        {
            isLiveTv = true;
            return (_libraryManager.GetItemById(program.ChannelId) as LiveTvChannel) is { } parent
                ? snapshot.Catalog.ResolveChannel(parent.ExternalId)
                : null;
        }

        isLiveTv = item is LiveTvChannel;
        return item is LiveTvChannel channel ? snapshot.Catalog.ResolveChannel(channel.ExternalId) : null;
    }

    /// <summary>
    /// Gets the tuner an open live stream is using.
    /// </summary>
    /// <param name="snapshot">The snapshot this decision uses.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="isLiveTv"><c>true</c> if the id belongs to the Live TV provider.</param>
    /// <returns>The tuner key, or <c>null</c> if not Live TV, not open, or unresolvable.</returns>
    public string? GetLiveStreamTuner(AccessSnapshot snapshot, string liveStreamId, out bool isLiveTv)
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
        return snapshot.Catalog.ResolveUrl(url);
    }

    private AccessSnapshot Build(PluginConfiguration config, long version)
    {
        void Warn(string message) => _logger.LogWarning("LiveTvAccess: {Warning}", message);

        // Bad tuner entries (invalid or reserved key, missing URL, duplicates) are skipped one by one.
        var catalog = TunerCatalog.FromEntries(
            (config.Tuners ?? []).Select(t => t is null ? null : new TunerEntry(t.Key, t.Url)),
            Warn);

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

        var map = UserTunerMap.FromEntries(catalog.Keys, entries, Warn);

        // Tag ownership: configured tuners + "unassigned" + every tag applied before (history).
        var history = (config.OwnedTagHistory ?? []).Where(TunerTags.IsManaged).ToArray();
        var owned = TagOwnership.Owned(catalog.Keys, history);
        var current = catalog.Keys.Select(TunerTags.ForTuner).Append(TunerTags.Unassigned);
        var newlyClaimed = current.Where(tag => !history.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (newlyClaimed.Count > 0)
        {
            // Remember them (so a later-removed tuner's tag is still cleaned up). SaveConfiguration doesn't raise
            // ConfigurationChanged, so this can't trigger another sync.
            config.OwnedTagHistory = [.. history, .. newlyClaimed];
            Plugin.Instance?.SaveConfiguration();
        }

        _logger.LogInformation(
            "LiveTvAccess: config loaded (version {Version}), tuners [{Tuners}], {Count} user assignment(s)",
            version,
            string.Join(", ", catalog.Keys),
            map.AssignedUserCount);
        return new AccessSnapshot(catalog, map, owned, newlyClaimed, version);
    }
}
