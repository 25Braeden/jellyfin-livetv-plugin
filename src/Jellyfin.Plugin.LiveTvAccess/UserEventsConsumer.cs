using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Re-syncs users when Jellyfin creates or deletes one. Jellyfin publishes these events from
/// Jellyfin.Server.Implementations/Users/UserManager.cs:322 and :373 and resolves consumers from DI
/// (Jellyfin.Server.Implementations/Events/EventManager.cs:46-52). Policy edits publish no event; see
/// <see cref="Filters.LiveTvAccessFilter"/> and the periodic pass in <see cref="TagSyncService"/>.
/// </summary>
public class UserEventsConsumer : IEventConsumer<UserCreatedEventArgs>, IEventConsumer<UserDeletedEventArgs>
{
    private readonly ISyncSignals _sync;
    private readonly AccessService _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserEventsConsumer"/> class.
    /// </summary>
    /// <param name="sync">The sync worker.</param>
    /// <param name="access">The access service.</param>
    public UserEventsConsumer(ISyncSignals sync, AccessService access)
    {
        _sync = sync;
        _access = access;
    }

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        _sync.MarkUsersDirty("user created");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        // The map may reference the deleted id; rebuild it (deleted ids are skipped).
        _access.Invalidate();
        _sync.MarkUsersDirty("user deleted");
        return Task.CompletedTask;
    }
}
