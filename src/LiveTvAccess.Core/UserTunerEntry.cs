using System;

namespace LiveTvAccess.Core;

/// <summary>
/// One configured user-to-tuner assignment, by user id.
/// </summary>
/// <param name="UserId">The user id; <see cref="Guid.Empty"/> if unresolved.</param>
/// <param name="TunerKey">The tuner key as configured.</param>
/// <param name="Label">Text identifying the entry in warnings (e.g. the username).</param>
public sealed record UserTunerEntry(Guid UserId, string? TunerKey, string? Label);
