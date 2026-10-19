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
}
