using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using LiveTvAccess.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Keeps Jellyfin's native visibility in sync with the plugin config: tags every LiveTvChannel with
/// "livetv-{tuner}" (or "livetv-unassigned") and sets each non-admin user's BlockedTags.
/// <para>
/// One tracked background worker does all passes. Triggers only set "users dirty" / "channels dirty"
/// flags and wake it, so bursts coalesce. Triggers: startup (both), guide refresh (channels), plugin config
/// change (both), user created/deleted (<see cref="UserEventsConsumer"/>), successful admin writes to
/// /Users/* (<see cref="Filters.LiveTvAccessFilter"/>), and a periodic users pass as a backstop, because
/// Jellyfin 10.11.11 publishes no event for policy edits (Jellyfin.Server.Implementations/Users/UserManager.cs:803).
/// Until the first full pass succeeds, <see cref="IsReady"/> is false and protected operations are denied
/// for non-admins; failed passes retry with backoff.
/// </para>
/// </summary>
public sealed class TagSyncService : IHostedService, ISyncSignals, IDisposable
{
    // RefreshGuideScheduledTask.Key (src/Jellyfin.LiveTv/Guide/RefreshGuideScheduledTask.cs:56).
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly AccessService _access;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<TagSyncService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private int _usersDirty;
    private int _channelsDirty;
    private int _ready;
    private int _passCount;
    private int _disposed;
    private string? _lastForeignReport;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagSyncService"/> class.
    /// </summary>
    /// <param name="access">The access service.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TagSyncService}"/> interface.</param>
    public TagSyncService(
        AccessService access,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ITaskManager taskManager,
        ILogger<TagSyncService> logger)
    {
        _access = access;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    /// <summary>Gets or sets the first retry delay after a failed pass (doubles up to <see cref="MaxRetryDelay"/>).</summary>
    internal TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets the longest retry delay.</summary>
    internal TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the interval of the periodic users pass (backstop for policy edits made outside the API).</summary>
    internal TimeSpan PeriodicInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets how many channels are processed per page.</summary>
    internal int PageSize { get; set; } = 500;

    /// <summary>Gets the number of completed passes (tests).</summary>
    internal int PassCount => Volatile.Read(ref _passCount);

    /// <inheritdoc />
    public void MarkUsersDirty(string reason)
    {
        Interlocked.Exchange(ref _usersDirty, 1);
        Wake();
    }

    /// <inheritdoc />
    public void MarkChannelsDirty(string reason)
    {
        Interlocked.Exchange(ref _channelsDirty, 1);
        Wake();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _logger.LogInformation("LiveTvAccess: waiting for the initial sync; protected Live TV operations are denied for non-admins until it completes");
        Interlocked.Exchange(ref _usersDirty, 1);
        Interlocked.Exchange(ref _channelsDirty, 1);

        // Never run the sync on the host's startup thread: the worker starts on the thread pool.
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            // Wait for the worker to finish; give up only if the host's shutdown deadline passes.
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stopping.Cancel();
        _worker?.Wait(TimeSpan.FromSeconds(10));
        _stopping.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the pending wake-up covers this request too.
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; nothing left to wake.
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Task.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal))
        {
            MarkChannelsDirty("guide refresh");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        _access.Invalidate();
        MarkUsersDirty("config change");
        MarkChannelsDirty("config change");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retryDelay = InitialRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            var users = Interlocked.Exchange(ref _usersDirty, 0) == 1;
            var channels = Interlocked.Exchange(ref _channelsDirty, 0) == 1;
            if (users || channels)
            {
                try
                {
                    if (!await PassAsync(users, channels, ct).ConfigureAwait(false))
                    {
                        // The config changed mid-pass; its results came from an old snapshot. Redo the work with the
                        // new one right away (not a failure: no backoff, and not counted towards "ready").
                        _logger.LogInformation("LiveTvAccess: sync pass superseded by a configuration change; restarting");
                        if (users)
                        {
                            Interlocked.Exchange(ref _usersDirty, 1);
                        }

                        if (channels)
                        {
                            Interlocked.Exchange(ref _channelsDirty, 1);
                        }

                        continue;
                    }

                    Interlocked.Increment(ref _passCount);
                    retryDelay = InitialRetryDelay;
                    if (channels && users && Interlocked.Exchange(ref _ready, 1) == 0)
                    {
                        _logger.LogInformation("LiveTvAccess: initial sync complete; Live TV access enforcement is active");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Put the work back and retry later. Until the first full pass succeeds we stay closed.
                    if (users)
                    {
                        Interlocked.Exchange(ref _usersDirty, 1);
                    }

                    if (channels)
                    {
                        Interlocked.Exchange(ref _channelsDirty, 1);
                    }

                    _logger.LogError(
                        ex,
                        "LiveTvAccess: sync failed; retrying in {Delay}{State}",
                        retryDelay,
                        IsReady ? string.Empty : " (still denying protected operations until the initial sync completes)");
                    try
                    {
                        await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
                }
            }

            try
            {
                // Sleep until woken, or until the periodic users pass is due.
                if (!await _wake.WaitAsync(PeriodicInterval, ct).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _usersDirty, 1);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns><c>false</c> if the pass was superseded by a configuration change.</returns>
    private async Task<bool> PassAsync(bool users, bool channels, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        // One snapshot for the whole pass; if the config changes mid-pass the pass is abandoned and redone.
        var pass = new PassContext(_access.Snapshot);
        // While channels may still carry old tags (a renamed or removed key), users also block the plugin's history
        // tags; once the channels pass has re-tagged everything, a final users pass drops them again.
        var updatedUsers = users ? await SyncUsersAsync(pass, includeHistory: channels, ct).ConfigureAwait(false) : 0;
        var usersDone = Stopwatch.GetTimestamp();
        var (pages, retagged, midPassUsers) = channels ? await SyncChannelsAsync(pass, ct).ConfigureAwait(false) : (0, 0, 0);
        if (IsSuperseded(pass))
        {
            return false;
        }

        if (channels)
        {
            updatedUsers += await SyncUsersAsync(pass, includeHistory: false, ct).ConfigureAwait(false);
            if (IsSuperseded(pass))
            {
                return false;
            }
        }

        _logger.LogInformation(
            "LiveTvAccess: sync pass: {Channels} channel(s) retagged in {Pages} page(s), {Users} user(s) updated; took {TotalMs} ms (users {UsersMs} ms, channels {ChannelsMs} ms)",
            retagged,
            pages,
            updatedUsers + midPassUsers,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (long)Stopwatch.GetElapsedTime(started, usersDone).TotalMilliseconds,
            (long)Stopwatch.GetElapsedTime(usersDone).TotalMilliseconds);
        Report(pass);
        return true;
    }

    private bool IsSuperseded(PassContext pass) => _access.Version != pass.Snapshot.Version;

    private void Report(PassContext pass)
    {
        if (pass.ClaimedInUse.Count > 0)
        {
            _logger.LogWarning(
                "LiveTvAccess: tag(s) the plugin just started managing were already in use: {Tags}. They are now managed by the plugin (expected once after upgrading from a version without tag history; otherwise check the tuner keys)",
                Describe(pass.ClaimedInUse));
        }

        // Foreign "livetv-*" tags are left alone. Report them when the set changes, not on every pass.
        var foreign = pass.Foreign.Count == 0 ? null : Describe(pass.Foreign);
        if (foreign is not null && !string.Equals(foreign, _lastForeignReport, StringComparison.Ordinal))
        {
            _logger.LogWarning("LiveTvAccess: found livetv-* tags the plugin doesn't own; left untouched: {Tags}", foreign);
        }

        _lastForeignReport = foreign;
    }

    private static string Describe(SortedDictionary<string, (int Users, int Channels)> counts)
        => string.Join(", ", counts.Select(c => $"{c.Key} (users {c.Value.Users}, channels {c.Value.Channels})"));

    private async Task<(int Pages, int Retagged, int Users)> SyncChannelsAsync(PassContext pass, CancellationToken ct)
    {
        var snapshot = pass.Snapshot;

        // Stable paging: fetch the channel ids once, then load and process them a page at a time.
        var ids = _libraryManager.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
        });

        var pages = 0;
        var retagged = 0;
        var users = 0;
        for (var start = 0; start < ids.Count; start += PageSize)
        {
            ct.ThrowIfCancellationRequested();
            if (IsSuperseded(pass))
            {
                break;
            }

            var page = ids.Skip(start).Take(PageSize).ToArray();
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel],
                ItemIds = page,
            });

            foreach (var channel in items.OfType<LiveTvChannel>())
            {
                var key = snapshot.Catalog.ResolveChannel(channel.ExternalId);
                var tag = key is null ? TunerTags.Unassigned : TunerTags.ForTuner(key);
                pass.NoteClaimedInUse(channel.Tags, isUser: false);
                var tags = TagOwnership.Merge(channel.Tags, snapshot.OwnedTags, [tag], out var foreign).ToArray();
                pass.NoteForeign(foreign, isUser: false);
                if (tags.SequenceEqual(channel.Tags, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The item may be Jellyfin's cached instance: if the save fails, restore the old tags so the
                // next pass sees the channel as still needing them (otherwise it would never be persisted).
                var previous = channel.Tags;
                channel.Tags = tags;
                try
                {
                    await channel.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                }
                catch
                {
                    channel.Tags = previous;
                    throw;
                }

                retagged++;
            }

            pages++;

            // A user change that arrived mid-pass is applied now, not after the remaining pages. If that fails, put the
            // user work back: the outer retry only re-queues what this pass started with.
            if (Interlocked.Exchange(ref _usersDirty, 0) == 1)
            {
                try
                {
                    users += await SyncUsersAsync(pass, includeHistory: true, ct).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Exchange(ref _usersDirty, 1);
                    throw;
                }
            }
        }

        return (pages, retagged, users);
    }

    private async Task<int> SyncUsersAsync(PassContext pass, bool includeHistory, CancellationToken ct)
    {
        var snapshot = pass.Snapshot;
        var changed = 0;
        foreach (var user in _userManager.GetUsers().ToList())
        {
            ct.ThrowIfCancellationRequested();

            // Admins are never restricted: strip our tags, keep any the admin set themselves.
            // With includeHistory, others' tags include the plugin's history tags: after a key rename, channels still
            // carry the old tag until the channels pass re-tags them.
            var desired = user.HasPermission(PermissionKind.IsAdministrator)
                ? []
                : includeHistory ? snapshot.Map.GetBlockedTags(user.Id, snapshot.OwnedTags) : snapshot.Map.GetBlockedTags(user.Id);
            var current = user.GetPreference(PreferenceKind.BlockedTags);
            pass.NoteClaimedInUse(current, isUser: true);
            var blocked = TagOwnership.Merge(current, snapshot.OwnedTags, desired, out var foreign).ToArray();
            pass.NoteForeign(foreign, isUser: true);
            if (blocked.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // UpdateUserAsync raises no OnUserUpdated in 10.11.11 (only RenameUser does, UserManager.cs:211),
            // and this service doesn't subscribe to OnUserUpdated, so its own writes never queue another pass.
            user.SetPreference(PreferenceKind.BlockedTags, blocked);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Per-pass state: the snapshot it uses and what it found.
    /// </summary>
    private sealed class PassContext
    {
        public PassContext(AccessSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public AccessSnapshot Snapshot { get; }

        public SortedDictionary<string, (int Users, int Channels)> Foreign { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedDictionary<string, (int Users, int Channels)> ClaimedInUse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void NoteForeign(IEnumerable<string> tags, bool isUser) => Count(Foreign, tags, isUser);

        public void NoteClaimedInUse(IEnumerable<string> existing, bool isUser)
            => Count(ClaimedInUse, existing.Where(Snapshot.NewlyClaimedTags.Contains), isUser);

        private static void Count(SortedDictionary<string, (int Users, int Channels)> counts, IEnumerable<string> tags, bool isUser)
        {
            foreach (var tag in tags)
            {
                var (u, c) = counts.GetValueOrDefault(tag);
                counts[tag] = isUser ? (u + 1, c) : (u, c + 1);
            }
        }
    }
}
