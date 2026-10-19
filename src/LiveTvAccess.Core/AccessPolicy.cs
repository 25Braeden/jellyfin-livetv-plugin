using System;

namespace LiveTvAccess.Core;

/// <summary>
/// The access rule: admins and API keys pass; everyone else only reaches their own tuner.
/// </summary>
public static class AccessPolicy
{
    /// <summary>
    /// Gets whether a caller is never restricted: administrators, and API keys that carry no user.
    /// The filter and <see cref="Decide(Caller, UserTunerMap, string?, bool)"/> both use this one rule.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <returns><c>true</c> if exempt.</returns>
    public static bool IsExempt(Caller caller)
    {
        return caller.IsAdministrator || (caller.IsApiKey && caller.UserId == Guid.Empty);
    }

    /// <summary>
    /// Decides whether a caller may use a channel or stream on a given tuner.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="targetTunerKey">The tuner the channel/stream belongs to, or <c>null</c> if unknown.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision Decide(Caller caller, UserTunerMap map, string? targetTunerKey)
        => Decide(caller, map, targetTunerKey, isReady: true);

    /// <summary>
    /// Decides whether a caller may use a channel or stream on a given tuner.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="targetTunerKey">The tuner the channel/stream belongs to, or <c>null</c> if unknown.</param>
    /// <param name="isReady">Whether the plugin's first sync has completed; until then non-admins are denied.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision Decide(Caller caller, UserTunerMap map, string? targetTunerKey, bool isReady)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (IsExempt(caller))
        {
            return AccessDecision.Allow;
        }

        if (!isReady)
        {
            return AccessDecision.DenyNotReady;
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
        AccessDecision.DenyNotReady => "Live TV is starting up. Try again in a moment.",
        _ => string.Empty,
    };
}
