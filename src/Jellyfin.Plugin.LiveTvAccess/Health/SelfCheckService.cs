using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using LiveTvAccess.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// Startup (and recurring) self-check. Verifies that the request filter is registered, that the Jellyfin endpoints,
/// models and claim types it relies on are still there, and that the configured tuners match Jellyfin's tuners and
/// channels. Any problem puts the plugin in a visible failed state: logged as an error, shown at
/// <c>GET /LiveTvAccess/Status</c>, and protected Live TV operations are denied for non-admins (the filter treats it
/// like "not ready"; admins and library use are unaffected). It never throws, never unloads the plugin, and
/// recovers by itself: it re-runs on plugin or Live TV configuration changes, after Refresh Guide, and periodically
/// (every minute while failed). Stream URLs listed by more than one tuner are a warning only.
/// </summary>
public sealed class SelfCheckService : ISelfCheck, IHostedService, IDisposable
{
    /// <summary>Trigger name of on-demand runs from the status API; they don't advance the claim-evidence window.</summary>
    public const string StatusApiTrigger = "status API";

    private const string RefreshGuideTaskKey = "RefreshGuide";
    private const int PageSize = 500;

    private readonly AccessService _access;
    private readonly ISyncSignals _sync;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly IJellyfinSurface _surface;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<SelfCheckService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private SelfCheckStatus _status = new();
    private string _pendingTrigger = "startup";
    private string _lastProblems = string.Empty;
    private string _lastWarnings = string.Empty;
    private long _lastRecognized;
    private long _lastUnrecognized;
    private Task? _worker;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelfCheckService"/> class.
    /// </summary>
    /// <param name="access">The access service.</param>
    /// <param name="sync">The tag-sync worker.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="surface">The Jellyfin surface the filter depends on.</param>
    /// <param name="tunerHostManager">The tuner host manager.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="logger">The logger.</param>
    public SelfCheckService(
        AccessService access,
        ISyncSignals sync,
        ILibraryManager libraryManager,
        IConfigurationManager configurationManager,
        IJellyfinSurface surface,
        ITunerHostManager tunerHostManager,
        ITaskManager taskManager,
        ILogger<SelfCheckService> logger)
    {
        _access = access;
        _sync = sync;
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _surface = surface;
        _tunerHostManager = tunerHostManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsHealthy => Volatile.Read(ref _status).State == SelfCheckStatus.Healthy;

    /// <inheritdoc />
    public SelfCheckStatus Status => Volatile.Read(ref _status);

    /// <summary>Gets or sets the interval between checks while healthy.</summary>
    internal TimeSpan HealthyInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the interval between checks while failed.</summary>
    internal TimeSpan FailedInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the delay before the first check (let the server finish starting).</summary>
    internal TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<SelfCheckStatus> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SelfCheckStatus status;
            var counts = _access.CallerCounts;
            try
            {
                status = await EvaluateAsync(trigger, counts, cancellationToken).ConfigureAwait(false);

                // Only completed scheduled / event runs consume the claim evidence: on-demand API refreshes and
                // cancelled runs leave the window open, so a broken claim contract can't be hidden by them.
                if (!string.Equals(trigger, StatusApiTrigger, StringComparison.Ordinal))
                {
                    (_lastRecognized, _lastUnrecognized) = counts;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Never let the check take the server down: a check that can't run is a failed state.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "LiveTvAccess: the self-check could not run");
                status = new SelfCheckStatus
                {
                    State = SelfCheckStatus.Failed,
                    CheckedAtUtc = DateTime.UtcNow,
                    Trigger = trigger,
                    InitialSyncComplete = _sync.IsReady,
                    RequestsChecked = LiveTvAccessFilter.RequestsChecked,
                    Checks = [new CheckResult("Self-check ran", false, ex.GetType().Name)],
                    Problems = [$"The self-check could not run ({ex.GetType().Name}); Live TV stays denied for non-admins until it passes."],
                };
            }

            Publish(status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnPluginConfigurationChanged;
        }

        _configurationManager.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
        _taskManager.TaskCompleted += OnTaskCompleted;
        _worker = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
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

        Unsubscribe();
        _stopping.Cancel();
        _worker?.Wait(TimeSpan.FromSeconds(10));
        _stopping.Dispose();
        _wake.Dispose();
        _gate.Dispose();
    }

    private void Unsubscribe()
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnPluginConfigurationChanged;
        }

        _configurationManager.NamedConfigurationUpdated -= OnNamedConfigurationUpdated;
        _taskManager.TaskCompleted -= OnTaskCompleted;
    }

    private void OnPluginConfigurationChanged(object? sender, BasePluginConfiguration e) => Wake("plugin configuration saved");

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (string.Equals(e.Key, "livetv", StringComparison.OrdinalIgnoreCase))
        {
            Wake("Live TV configuration saved");
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Task.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal))
        {
            Wake("guide refresh");
        }
    }

    private void Wake(string trigger)
    {
        Volatile.Write(ref _pendingTrigger, trigger);
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
            // Already signalled; the pending run covers this change too.
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var trigger = Interlocked.Exchange(ref _pendingTrigger, "scheduled");
                var status = await RunAsync(trigger, ct).ConfigureAwait(false);
                var interval = status.State == SelfCheckStatus.Healthy ? HealthyInterval : FailedInterval;
                if (await _wake.WaitAsync(interval, ct).ConfigureAwait(false))
                {
                    // Let the change settle (the access snapshot is rebuilt on the same event).
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<SelfCheckStatus> EvaluateAsync(string trigger, (long Recognized, long Unrecognized) counts, CancellationToken ct)
    {
        var checks = new List<CheckResult>();
        var problems = new List<string>();
        var warnings = new List<string>();

        // 1. The filter: without it nothing is enforced (only the tag-based channel hiding still applies).
        var registered = _surface.IsFilterRegistered();
        checks.Add(new CheckResult("Access filter registered", registered, registered ? "global MVC action filter present" : "not in MvcOptions.Filters"));
        if (!registered)
        {
            problems.Add("The LiveTvAccess request filter is not registered, so requests are NOT being checked. Restart Jellyfin; if this persists, remove the plugin.");
        }

        // 2. Endpoint shapes and models the filter and the response scrubber rely on.
        var drift = EndpointContract.FindMissing(_surface.GetActions()).Concat(EndpointContract.FindMissingModelMembers()).ToArray();
        checks.Add(new CheckResult(
            "Endpoint shapes",
            drift.Length == 0,
            drift.Length == 0 ? $"{EndpointContract.Requirements.Count} endpoints as expected" : string.Join("; ", drift)));
        problems.AddRange(drift.Select(d => "Jellyfin endpoint drift: " + d));

        // 3. Claim contract: the constants, and what requests actually carried since the last check.
        var (claimOk, claimDetail) = CheckClaims(counts);
        checks.Add(new CheckResult("Claim contract", claimOk, claimDetail));
        if (!claimOk)
        {
            problems.Add("Claim contract broken: " + claimDetail);
        }

        // 4. Tuners against Jellyfin's tuners and the library's channels.
        var snapshot = _access.Snapshot;
        var jellyfinUrls = (_configurationManager.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? [])
            .Where(h => string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Url)
            .ToArray();
        var tuners = TunerHealth.Evaluate(snapshot.Catalog, snapshot.ConfiguredTunerEntries, snapshot.Map, jellyfinUrls, ChannelExternalIds());
        checks.Add(new CheckResult(
            "Tuners match Jellyfin's tuners and channels",
            tuners.Problems.Count == 0,
            tuners.Tuners.Count == 0 ? "no tuners configured" : string.Join("; ", tuners.Tuners.Select(t => $"{t.Key}: {t.ChannelCount} channels, {t.UserCount} users{(t.InJellyfin ? string.Empty : ", not a Jellyfin tuner")}"))));
        problems.AddRange(tuners.Problems);
        warnings.AddRange(tuners.Warnings);

        // 5. Stream URLs on more than one tuner: a warning, never the failed state.
        var duplicates = await DescribeDuplicatesAsync(tuners.Duplicates, ct).ConfigureAwait(false);
        checks.Add(new CheckResult(
            "No stream URL listed by two tuners (warning only)",
            duplicates.Count == 0,
            duplicates.Count == 0 ? "none" : $"{duplicates.Count} stream URL(s) listed by more than one tuner"));

        return new SelfCheckStatus
        {
            State = problems.Count == 0 ? SelfCheckStatus.Healthy : SelfCheckStatus.Failed,
            CheckedAtUtc = DateTime.UtcNow,
            Trigger = trigger,
            InitialSyncComplete = _sync.IsReady,
            RequestsChecked = LiveTvAccessFilter.RequestsChecked,
            Checks = checks,
            Problems = problems,
            Warnings = warnings,
            Tuners = tuners.Tuners,
            Duplicates = duplicates,
        };
    }

    private (bool Ok, string Detail) CheckClaims((long Recognized, long Unrecognized) counts)
    {
        var claims = _surface.GetClaimTypes();
        var newRecognized = counts.Recognized - _lastRecognized;
        var newUnrecognized = counts.Unrecognized - _lastUnrecognized;

        if (claims is null)
        {
            return (false, "Jellyfin's claim type constants (InternalClaimTypes) were not found");
        }

        if (!string.Equals(claims.Value.UserId, AccessService.UserIdClaim, StringComparison.Ordinal)
            || !string.Equals(claims.Value.IsApiKey, AccessService.IsApiKeyClaim, StringComparison.Ordinal))
        {
            return (false, $"Jellyfin's claim types are '{claims.Value.UserId}' / '{claims.Value.IsApiKey}', the plugin expects '{AccessService.UserIdClaim}' / '{AccessService.IsApiKeyClaim}'");
        }

        // Every authenticated request since the last check carried no recognizable claim: the auth handler changed.
        if (newUnrecognized >= 3 && newRecognized == 0)
        {
            return (false, $"{newUnrecognized} authenticated requests since the last check carried no recognizable user or API-key claim, and none did");
        }

        return (true, $"{AccessService.UserIdClaim} / {AccessService.IsApiKeyClaim}");
    }

    private List<string?> ChannelExternalIds()
    {
        var ids = _libraryManager.GetItemIds(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] });
        var externalIds = new List<string?>(ids.Count);
        for (var start = 0; start < ids.Count; start += PageSize)
        {
            var page = ids.Skip(start).Take(PageSize).ToArray();
            externalIds.AddRange(_libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel], ItemIds = page })
                .OfType<LiveTvChannel>()
                .Select(c => c.ExternalId));
        }

        return externalIds;
    }

    /// <summary>
    /// Finds each duplicated stream's host (scheme and host only) from Jellyfin's cached M3U channel list. Only runs
    /// when there are duplicates; on a cold cache Jellyfin reads the playlists once, as a guide refresh does.
    /// </summary>
    private async Task<IReadOnlyList<DuplicateStreamInfo>> DescribeDuplicatesAsync(IReadOnlyList<DuplicateStreamUrl> duplicates, CancellationToken ct)
    {
        if (duplicates.Count == 0)
        {
            return [];
        }

        var wanted = duplicates.Select(d => d.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            foreach (var tunerHost in _tunerHostManager.TunerHosts.Where(h => string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var channel in await tunerHost.GetChannels(true, timeout.Token).ConfigureAwait(false))
                {
                    if (JellyfinIds.M3uMediaSourceId(channel.Id) is { } source && wanted.Contains(source))
                    {
                        hosts.TryAdd(source, Redact.Location(channel.Path));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The host is only a display aid.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("LiveTvAccess: could not look up the hosts of duplicated stream URLs ({Error})", ex.GetType().Name);
        }

        return duplicates
            .Select(d => new DuplicateStreamInfo(hosts.GetValueOrDefault(d.SourceId, "(unknown)"), d.SourceHash, d.Tuners, d.ChannelCount))
            .ToArray();
    }

    private void Publish(SelfCheckStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        var problems = string.Join(" | ", status.Problems);
        if (status.State == SelfCheckStatus.Failed)
        {
            if (previous.State != SelfCheckStatus.Failed || !string.Equals(problems, _lastProblems, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "LiveTvAccess: SELF-CHECK FAILED ({Count} problem(s)); protected Live TV operations are denied for non-admins until this is fixed (admins are unaffected). See GET /LiveTvAccess/Status. {Problems}",
                    status.Problems.Count,
                    problems);
            }
        }
        else if (previous.State == SelfCheckStatus.Failed)
        {
            _logger.LogInformation("LiveTvAccess: self-check passed again; Live TV access restored for mapped users");
        }
        else if (previous.State == SelfCheckStatus.Pending)
        {
            _logger.LogInformation(
                "LiveTvAccess: self-check passed: {Tuners}",
                string.Join(", ", status.Tuners.Select(t => $"{t.Key} ({t.ChannelCount} channels, {t.UserCount} users)")));
        }

        _lastProblems = problems;
        var warnings = string.Join(" | ", status.Warnings) + string.Join(",", status.Duplicates.Select(d => d.Hash));
        if (!string.Equals(warnings, _lastWarnings, StringComparison.Ordinal))
        {
            foreach (var warning in status.Warnings)
            {
                _logger.LogWarning("LiveTvAccess: self-check warning: {Warning}", warning);
            }

            foreach (var d in status.Duplicates)
            {
                _logger.LogWarning(
                    "LiveTvAccess: stream URL {Hash} (host {Host}) is listed by tuners {Tuners} on {Channels} channel(s); users of those tuners block each other on it",
                    d.Hash,
                    d.Host,
                    string.Join(", ", d.Tuners),
                    d.ChannelCount);
            }
        }

        _lastWarnings = warnings;
    }
}
