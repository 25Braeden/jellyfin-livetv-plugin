using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Health;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveTvAccess.Api;

/// <summary>
/// Admin-only plugin API: the self-check status (no URLs or credentials).
/// </summary>
[ApiController]
[Route("LiveTvAccess")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LiveTvAccessController : ControllerBase
{
    private readonly ISelfCheck _selfCheck;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvAccessController"/> class.
    /// </summary>
    /// <param name="selfCheck">The self-check.</param>
    public LiveTvAccessController(ISelfCheck selfCheck)
    {
        _selfCheck = selfCheck;
    }

    /// <summary>
    /// Gets the self-check status.
    /// </summary>
    /// <param name="refresh">Run the check now instead of returning the last result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SelfCheckStatus>> GetStatus([FromQuery] bool refresh, CancellationToken cancellationToken)
        => refresh ? await _selfCheck.RunAsync("status API", cancellationToken).ConfigureAwait(false) : _selfCheck.Status;
}
