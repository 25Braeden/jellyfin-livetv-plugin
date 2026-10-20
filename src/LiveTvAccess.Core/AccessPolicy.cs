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

    /// <summary>
    /// Decides one classified identifier found in a request (default-deny surface).
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="target">The classified identifier.</param>
    /// <param name="isReady">Whether the plugin's first sync has completed.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision DecideTarget(Caller caller, UserTunerMap map, TargetInfo target, bool isReady)
    {
        if (IsExempt(caller))
        {
            return AccessDecision.Allow;
        }

        return target.Kind switch
        {
            TargetKind.NotLiveTv => AccessDecision.Allow,
            TargetKind.Unresolved => target.MustResolve ? AccessDecision.DenyUnresolved : AccessDecision.Allow,
            TargetKind.Recording => AccessDecision.DenyRecording,
            _ => Decide(caller, map, target.TunerKey, isReady),
        };
    }

    /// <summary>
    /// Decides a Live TV endpoint itself: non-admins may only use allowlisted actions, and only with a tuner,
    /// native Live TV permission, and after the first sync.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="map">The user-to-tuner map.</param>
    /// <param name="isAllowlisted">Whether the action is on the non-admin allowlist.</param>
    /// <param name="isReady">Whether the plugin's first sync has completed.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision DecideEndpoint(Caller caller, UserTunerMap map, bool isAllowlisted, bool isReady)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (IsExempt(caller))
        {
            return AccessDecision.Allow;
        }

        if (!isAllowlisted)
        {
            return AccessDecision.DenyEndpoint;
        }

        // Same checks as for one of the user's own channels: ready, native permission, and having a tuner.
        return Decide(caller, map, map.GetTuner(caller.UserId), isReady);
    }
}
