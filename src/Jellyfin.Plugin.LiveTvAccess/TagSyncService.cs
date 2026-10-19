using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
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
/// Keeps Jellyfin's native visibility in sync with the plugin config, so channel lists, guide and
/// On Now are filtered per user without any external script:
/// tags every LiveTvChannel with "livetv-{tuner}" (or "livetv-unassigned"), and sets each non-admin
/// user's BlockedTags to every other tuner's tag. Runs at startup, after every guide refresh (channels
/// are re-created when stream URLs change), on plugin config change, and when a user is updated.
/// </summary>
public sealed class TagSyncService : IHostedService, IDisposable
{
    // RefreshGuideScheduledTask.Key (src/Jellyfin.LiveTv/Guide/RefreshGuideScheduledTask.cs:56).
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly AccessService _access;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<TagSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        _userManager.OnUserUpdated += OnUserUpdated;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _ = RunAsync("startup", channels: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _userManager.OnUserUpdated -= OnUserUpdated;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Task.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal))
        {
            _ = RunAsync("guide refresh", channels: true);
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        _access.Invalidate();
        _ = RunAsync("config change", channels: true);
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        _access.Invalidate();
        _ = RunAsync("user updated", channels: false);
    }

    private async Task RunAsync(string reason, bool channels)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tagged = channels ? await SyncChannelsAsync().ConfigureAwait(false) : 0;
            var users = await SyncUsersAsync().ConfigureAwait(false);
            _logger.LogInformation(
                "LiveTvAccess: sync ({Reason}): {Channels} channel(s) retagged, {Users} user(s) updated",
                reason,
                tagged,
                users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveTvAccess: sync ({Reason}) failed", reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> SyncChannelsAsync()
    {
        var catalog = _access.Catalog;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
        });

        var changed = 0;
        foreach (var channel in items.OfType<LiveTvChannel>())
        {
            var key = catalog.ResolveChannel(channel.ExternalId);
            var tag = key is null ? TunerTags.Unassigned : TunerTags.ForTuner(key);
            var tags = UserTunerMap.MergeTags(channel.Tags, [tag]).ToArray();
            if (tags.SequenceEqual(channel.Tags, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            channel.Tags = tags;
            await channel.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    private async Task<int> SyncUsersAsync()
    {
        var map = _access.Map;
        var changed = 0;
        foreach (var user in _userManager.GetUsers().ToList())
        {
            // Admins are never restricted: strip our tags, keep any the admin set themselves.
            var managed = user.HasPermission(PermissionKind.IsAdministrator) ? [] : map.GetBlockedTags(user.Id);
            var current = user.GetPreference(PreferenceKind.BlockedTags);
            var blocked = UserTunerMap.MergeTags(current, managed).ToArray();
            if (blocked.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            user.SetPreference(PreferenceKind.BlockedTags, blocked);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }
}
