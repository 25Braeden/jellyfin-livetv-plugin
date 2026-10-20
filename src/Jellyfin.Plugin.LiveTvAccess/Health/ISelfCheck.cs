using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// The plugin's self-check. While it isn't healthy (not run yet, or failed), protected Live TV operations are denied
/// for non-admins; admins are unaffected.
/// </summary>
public interface ISelfCheck
{
    /// <summary>Gets a value indicating whether the last check passed.</summary>
    bool IsHealthy { get; }

    /// <summary>Gets the last result.</summary>
    SelfCheckStatus Status { get; }

    /// <summary>
    /// Runs the check now.
    /// </summary>
    /// <param name="trigger">What triggered it (for the status and log).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<SelfCheckStatus> RunAsync(string trigger, CancellationToken cancellationToken);
}
