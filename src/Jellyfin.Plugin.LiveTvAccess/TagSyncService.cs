using System;
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
                    await PassAsync(users, channels, ct).ConfigureAwait(false);
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

    private async Task PassAsync(bool users, bool channels, CancellationToken ct)
    {
        var updatedUsers = users ? await SyncUsersAsync(ct).ConfigureAwait(false) : 0;
        var (pages, retagged, midPassUsers) = channels ? await SyncChannelsAsync(ct).ConfigureAwait(false) : (0, 0, 0);
        _logger.LogInformation(
            "LiveTvAccess: sync pass: {Channels} channel(s) retagged in {Pages} page(s), {Users} user(s) updated",
            retagged,
            pages,
            updatedUsers + midPassUsers);
    }

    private async Task<(int Pages, int Retagged, int Users)> SyncChannelsAsync(CancellationToken ct)
    {
        var catalog = _access.Catalog;

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
            var page = ids.Skip(start).Take(PageSize).ToArray();
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel],
                ItemIds = page,
            });

            foreach (var channel in items.OfType<LiveTvChannel>())
            {
                var key = catalog.ResolveChannel(channel.ExternalId);
                var tag = key is null ? TunerTags.Unassigned : TunerTags.ForTuner(key);
                var tags = UserTunerMap.MergeTags(channel.Tags, [tag]).ToArray();
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

            // A user change that arrived mid-pass is applied now, not after the remaining pages.
            if (Interlocked.Exchange(ref _usersDirty, 0) == 1)
            {
                users += await SyncUsersAsync(ct).ConfigureAwait(false);
            }
        }

        return (pages, retagged, users);
    }

    private async Task<int> SyncUsersAsync(CancellationToken ct)
    {
        var map = _access.Map;
        var changed = 0;
        foreach (var user in _userManager.GetUsers().ToList())
        {
            ct.ThrowIfCancellationRequested();

            // Admins are never restricted: strip our tags, keep any the admin set themselves.
            var managed = user.HasPermission(PermissionKind.IsAdministrator) ? [] : map.GetBlockedTags(user.Id);
            var current = user.GetPreference(PreferenceKind.BlockedTags);
            var blocked = UserTunerMap.MergeTags(current, managed).ToArray();
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
}
