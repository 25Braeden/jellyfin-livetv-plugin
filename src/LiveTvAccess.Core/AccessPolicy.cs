using System;

namespace LiveTvAccess.Core;

/// <summary>
/// The access rule: admins and API keys pass; everyone else only reaches their own tuner.
/// </summary>
public static class AccessPolicy
{
    /// <summary>
    /// Decides whether a caller may use a channel or stream on a given tuner.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="targetTunerKey">The tuner the channel/stream belongs to, or <c>null</c> if unknown.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision Decide(Caller caller, UserTunerMap map, string? targetTunerKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (caller.IsAdministrator || (caller.IsApiKey && caller.UserId == Guid.Empty))
        {
            return AccessDecision.Allow;
        }

        var own = map.GetTuner(caller.UserId);
        if (own is null)
        {
            return AccessDecision.DenyNoTuner;
        }

        if (targetTunerKey is null)
        {
            return AccessDecision.DenyUnknownTuner;
        }

        return string.Equals(own, targetTunerKey, StringComparison.Ordinal)
            ? AccessDecision.Allow
            : AccessDecision.DenyOtherTuner;
    }

    /// <summary>
    /// Gets the plain message returned to the client for a denial.
    /// </summary>
    /// <param name="decision">The decision.</param>
    /// <returns>The message.</returns>
    public static string Message(AccessDecision decision) => decision switch
    {
        AccessDecision.DenyNoTuner => "Live TV is not available for your account.",
        AccessDecision.DenyOtherTuner => "This channel is not available for your account.",
        AccessDecision.DenyUnknownTuner => "This channel is not available: its tuner is not configured.",
        _ => string.Empty,
    };
}
