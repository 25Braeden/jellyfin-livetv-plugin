namespace LiveTvAccess.Core;

/// <summary>
/// Outcome of an access check.
/// </summary>
public enum AccessDecision
{
    /// <summary>Let the request through.</summary>
    Allow,

    /// <summary>The user has no tuner assigned.</summary>
    DenyNoTuner,

    /// <summary>The channel/stream belongs to a tuner other than the user's.</summary>
    DenyOtherTuner,

    /// <summary>The plugin could not tell which tuner the channel/stream belongs to (fail closed).</summary>
    DenyUnknownTuner,

    /// <summary>The plugin has not completed its first sync yet (fail closed).</summary>
    DenyNotReady,

    /// <summary>The user lacks Jellyfin's native Live TV permission, or couldn't be identified.</summary>
    DenyNoLiveTvAccess,

    /// <summary>The Live TV endpoint isn't on the non-admin allowlist.</summary>
    DenyEndpoint,

    /// <summary>An identifier that must resolve didn't (route content id, token- or stream-shaped value).</summary>
    DenyUnresolved,

    /// <summary>The target is a Live TV recording (not mapped to tuners).</summary>
    DenyRecording,

    /// <summary>An endpoint that needs a Live TV target had none.</summary>
    DenyMissingTarget,
}
