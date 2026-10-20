using System;
using System.Collections.Generic;
using LiveTvAccess.Core;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// The self-check result shown at <c>GET /LiveTvAccess/Status</c>. Never contains URLs or credentials.
/// </summary>
public sealed class SelfCheckStatus
{
    /// <summary>State: not run yet.</summary>
    public const string Pending = "Pending";

    /// <summary>State: all checks passed.</summary>
    public const string Healthy = "Healthy";

    /// <summary>State: drift or a bad configuration; Live TV is denied for non-admins.</summary>
    public const string Failed = "Failed";

    /// <summary>Gets the state: <see cref="Pending"/>, <see cref="Healthy"/> or <see cref="Failed"/>.</summary>
    public string State { get; init; } = Pending;

    /// <summary>Gets when the check ran (UTC).</summary>
    public DateTime? CheckedAtUtc { get; init; }

    /// <summary>Gets what triggered the check.</summary>
    public string? Trigger { get; init; }

    /// <summary>Gets a value indicating whether the initial tag sync has completed (until then Live TV is denied for non-admins too).</summary>
    public bool InitialSyncComplete { get; init; }

    /// <summary>Gets the individual checks.</summary>
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    /// <summary>Gets the problems (any problem means <see cref="Failed"/>).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Gets the warnings (not failures).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Gets the per-tuner status.</summary>
    public IReadOnlyList<TunerStatus> Tuners { get; init; } = [];

    /// <summary>Gets the stream URLs listed by more than one tuner.</summary>
    public IReadOnlyList<DuplicateStreamInfo> Duplicates { get; init; } = [];

    /// <summary>Gets how many requests the filter has checked since the server started.</summary>
    public long RequestsChecked { get; init; }
}
