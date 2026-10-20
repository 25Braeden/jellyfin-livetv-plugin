using System;

namespace LiveTvAccess.Core;

/// <summary>
/// The access rule: admins and API keys pass; everyone else only reaches their own tuner.
/// </summary>
public static class AccessPolicy
{
    /// <summary>
    /// The one message returned to clients for every denial. The reason is logged server-side only, so a
    /// response can't be used to learn whether a channel or stream exists or which tuner it is on.
    /// </summary>
    public const string DeniedMessage = "This Live TV channel isn't available for your account.";

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

        // Jellyfin's own "Allow Live TV access" permission applies on top of the tuner map. Callers the plugin
        // can't identify have it false.
        if (!caller.HasLiveTvAccess)
        {
            return AccessDecision.DenyNoLiveTvAccess;
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
    /// Gets the plain message returned to the client for a decision.
    /// </summary>
    /// <param name="decision">The decision.</param>
    /// <returns><see cref="DeniedMessage"/> for every denial; empty for <see cref="AccessDecision.Allow"/>.</returns>
    public static string Message(AccessDecision decision)
        => decision == AccessDecision.Allow ? string.Empty : DeniedMessage;
}
