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
using MediaBrowser.Controller.MediaEncoding;
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
    private readonly ITranscodeManager _transcodeManager;
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
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AccessService}"/> interface.</param>
    public AccessService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        IConfigurationManager configurationManager,
        ITranscodeManager transcodeManager,
        ILogger<AccessService> logger)
    {
        _transcodeManager = transcodeManager;
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
    /// Gets the caller from the request principal: <c>null</c> for anonymous requests (the filter then applies
    /// default-deny to them as a caller with no user and no Live TV access). An authenticated principal without a
    /// usable user claim, or whose user no longer exists, becomes a caller with no Live TV access too.
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
    /// Classifies one identifier found in a request: Live TV channel/program/stream (with its tuner), recording,
    /// something else (library item, user, non-tuner stream), or unresolved.
    /// </summary>
    /// <param name="snapshot">The snapshot this decision uses.</param>
    /// <param name="value">The identifier.</param>
    /// <returns>The classification.</returns>
    public TargetInfo Classify(AccessSnapshot snapshot, Filters.CollectedValue value)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            return ClassifyCore(snapshot, value);
        }
#pragma warning disable CA1031 // Any lookup failure means we can't classify: fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Jellyfin's lookups can throw (e.g. the placeholder row 00000000000000000000000000000001 can't be
            // deserialized). Unclassifiable is denied, whatever kind of argument it came from.
            _logger.LogDebug(ex, "LiveTvAccess: could not classify a request identifier; denying");
            return new TargetInfo(TargetKind.Unresolved, null, MustResolve: true);
        }
    }

    private TargetInfo ClassifyCore(AccessSnapshot snapshot, Filters.CollectedValue value)
    {
        // A play session id makes streaming reuse that session's transcoding job and its media source
        // (Jellyfin.Api/Helpers/StreamingHelpers.cs:117-125). If the job is on a live stream, check that stream.
        if (value.Hint == Filters.ValueHint.PlaySessionId)
        {
            var job = _transcodeManager.GetTranscodingJob(value.Raw ?? value.Text);
            if (job is not null)
            {
                var jobStream = !string.IsNullOrEmpty(job.LiveStreamId) ? job.LiveStreamId : job.MediaSource?.LiveStreamId;
                return string.IsNullOrEmpty(jobStream)
                    ? new TargetInfo(TargetKind.NotLiveTv, null, false)
                    : ClassifyStream(snapshot, jobStream, ProviderString.Classify(jobStream, out _));
            }

            if (value.Guid is null)
            {
                // Not a running session and not GUID-shaped: nothing to reuse.
                return new TargetInfo(TargetKind.NotLiveTv, null, false);
            }
        }

        if (value.Guid is { } guid)
        {
            if (guid == Guid.Empty)
            {
                // Jellyfin uses the empty id for "none" / the user's root folder.
                return new TargetInfo(TargetKind.NotLiveTv, null, value.IsRouteIdentifier);
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is LiveTvChannel or LiveTvProgram)
            {
                return new TargetInfo(TargetKind.LiveTv, GetItemTuner(snapshot, guid, out _), value.IsRouteIdentifier);
            }

            if (item is not null)
            {
                return new TargetInfo(IsRecording(item) ? TargetKind.Recording : TargetKind.NotLiveTv, null, value.IsRouteIdentifier);
            }

            return _userManager.GetUserById(guid) is not null
                ? new TargetInfo(TargetKind.NotLiveTv, null, value.IsRouteIdentifier)
                : new TargetInfo(TargetKind.Unresolved, null, value.IsRouteIdentifier);
        }

        switch (ProviderString.Classify(value.Text, out var tokenItemId))
        {
            case ProviderStringKind.LiveTvOpenToken:
                // A Live TV open token must name a channel we can place on a tuner. (Jellyfin throws on an empty id.)
                return tokenItemId != Guid.Empty && _libraryManager.GetItemById(tokenItemId) is LiveTvChannel
                    ? new TargetInfo(TargetKind.LiveTv, GetItemTuner(snapshot, tokenItemId, out _), true)
                    : new TargetInfo(TargetKind.Unresolved, null, true);

            case ProviderStringKind.LiveTvStreamId:
            case ProviderStringKind.OtherProvider:
                return ClassifyStream(snapshot, value.Text, ProviderString.Classify(value.Text, out _));

            default:
                return new TargetInfo(value.IsRouteIdentifier ? TargetKind.Unresolved : TargetKind.NotLiveTv, null, value.IsRouteIdentifier);
        }
    }

    /// <summary>
    /// Gets whether an item is a Live TV recording: an active one (Video.SourceType is LiveTV,
    /// MediaBrowser.Controller/Entities/Video.cs:168-177) or a file under a recording folder
    /// (src/Jellyfin.LiveTv/Recordings/RecordingsManager.cs:106-170).
    /// </summary>
    private bool IsRecording(MediaBrowser.Controller.Entities.BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.Video video && video.SourceType == MediaBrowser.Controller.Entities.SourceType.LiveTV)
        {
            return true;
        }

        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var options = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");
        var roots = new[]
        {
            options.RecordingPath,
            options.MovieRecordingPath,
            options.SeriesRecordingPath,
            System.IO.Path.Combine(_configurationManager.CommonApplicationPaths.DataPath, "livetv", "recordings"),
        };
        var path = item.Path.Replace('\\', '/');
        return roots.Any(root => !string.IsNullOrWhiteSpace(root)
            && path.StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the running HLS transcoding job whose playlist is exactly <c>{transcode folder}/{name}.m3u8</c>
    /// (ITranscodeManager.GetTranscodingJob(path, type): exact path match, MediaBrowser.MediaEncoding/Transcoding/
    /// TranscodeManager.cs:109-115). The name must already be validated with <see cref="HlsNames.IsSafeName"/>.
    /// </summary>
    /// <param name="name">The playlist name.</param>
    /// <returns>The job, or <c>null</c> if none is running.</returns>
    public TranscodingJob? FindHlsJob(string name)
    {
        try
        {
            var path = System.IO.Path.Combine(TranscodePath(), name + ".m3u8");
            return _transcodeManager.GetTranscodingJob(path, TranscodingJobType.Hls);
        }
#pragma warning disable CA1031 // Lookup failed: treat as no job (denied).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "LiveTvAccess: HLS job lookup failed");
            return null;
        }
    }

    /// <summary>
    /// Classifies a running transcoding job: on a live stream, that stream (and its tuner); otherwise not Live TV.
    /// </summary>
    /// <param name="snapshot">The snapshot this decision uses.</param>
    /// <param name="job">The job.</param>
    /// <returns>The classification.</returns>
    public TargetInfo ClassifyJob(AccessSnapshot snapshot, TranscodingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var stream = !string.IsNullOrEmpty(job.LiveStreamId) ? job.LiveStreamId : job.MediaSource?.LiveStreamId;
        return string.IsNullOrEmpty(stream)
            ? new TargetInfo(TargetKind.NotLiveTv, null, true)
            : ClassifyStream(snapshot, stream, ProviderString.Classify(stream, out _));
    }

    /// <summary>
    /// The transcode folder, as Jellyfin computes it (MediaBrowser.Common/Configuration/EncodingConfigurationExtensions.cs:29-40)
    /// but without its side effect of creating a marker file.
    /// </summary>
    private string TranscodePath()
    {
        var configured = _configurationManager.GetConfiguration<MediaBrowser.Model.Configuration.EncodingOptions>("encoding").TranscodingTempPath;
        return string.IsNullOrEmpty(configured)
            ? System.IO.Path.Combine(_configurationManager.CommonApplicationPaths.CachePath, "transcodes")
            : configured;
    }

    /// <summary>
    /// Classifies a live stream id (or other provider-prefixed string): it must be open; a tuner stream is placed on
    /// its tuner; an open stream of another provider without a tuner isn't Live TV.
    /// </summary>
    private TargetInfo ClassifyStream(AccessSnapshot snapshot, string streamId, ProviderStringKind kind)
    {
        var stream = _mediaSourceManager.GetLiveStreamInfo(streamId.Trim());
        if (stream is null)
        {
            return new TargetInfo(TargetKind.Unresolved, null, true);
        }

        if (string.IsNullOrEmpty(stream.TunerHostId))
        {
            // An open stream that isn't on a tuner (another provider). A Live TV stream without a tuner is
            // unexpected: fail closed.
            return new TargetInfo(kind == ProviderStringKind.OtherProvider ? TargetKind.NotLiveTv : TargetKind.Unresolved, null, true);
        }

        return new TargetInfo(TargetKind.LiveTv, TunerOfHost(snapshot, stream.TunerHostId), true);
    }

    private string? TunerOfHost(AccessSnapshot snapshot, string tunerHostId)
    {
        var url = _configurationManager.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
            .FirstOrDefault(h => string.Equals(h.Id, tunerHostId, StringComparison.OrdinalIgnoreCase))?.Url;
        return snapshot.Catalog.ResolveUrl(url);
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
