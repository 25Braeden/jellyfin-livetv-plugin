namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// The tag-sync worker as seen by the rest of the plugin: readiness, and requests for another pass.
/// </summary>
public interface ISyncSignals
{
    /// <summary>
    /// Gets a value indicating whether the first full sync has completed. Until then protected
    /// operations are denied for non-admins.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Requests a users pass (blocked tags). Coalesced: many requests before the worker runs make one pass.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    void MarkUsersDirty(string reason);

    /// <summary>
    /// Requests a channels pass (tuner tags). Coalesced like <see cref="MarkUsersDirty"/>.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    void MarkChannelsDirty(string reason);
}
