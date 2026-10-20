namespace LiveTvAccess.Core;

/// <summary>
/// What an identifier found in a request turned out to be.
/// </summary>
public enum TargetKind
{
    /// <summary>Resolved to something that isn't Live TV (a library item, a user, a non-tuner stream).</summary>
    NotLiveTv,

    /// <summary>A Live TV channel, program or tuner stream; <see cref="TargetInfo.TunerKey"/> says which tuner.</summary>
    LiveTv,

    /// <summary>A Live TV recording (active or finished). Not mapped to tuners: denied for non-admins.</summary>
    Recording,

    /// <summary>Could not be resolved.</summary>
    Unresolved,
}

/// <summary>
/// One classified identifier.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="TunerKey">For <see cref="TargetKind.LiveTv"/>: the tuner, or <c>null</c> if unknown.</param>
/// <param name="MustResolve">Whether the request is denied if this identifier can't be resolved
/// (route content identifiers and anything shaped like a Live TV token or stream id).</param>
public readonly record struct TargetInfo(TargetKind Kind, string? TunerKey, bool MustResolve);
