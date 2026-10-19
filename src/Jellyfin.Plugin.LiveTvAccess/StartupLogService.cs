using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Logs one line at server start to prove the plugin's DI registrations are live.
/// </summary>
public class StartupLogService : IHostedService
{
    private readonly ILogger<StartupLogService> _logger;
    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupLogService"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{StartupLogService}"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    public StartupLogService(ILogger<StartupLogService> logger, IServerApplicationHost appHost)
    {
        _logger = logger;
        _appHost = appHost;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("LiveTvAccess started on Jellyfin {Version}", _appHost.ApplicationVersionString);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
