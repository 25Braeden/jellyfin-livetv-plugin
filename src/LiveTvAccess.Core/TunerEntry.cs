namespace LiveTvAccess.Core;

/// <summary>
/// One configured tuner.
/// </summary>
/// <param name="Key">The tuner key as configured.</param>
/// <param name="Url">The tuner's M3U URL as configured in Jellyfin.</param>
public sealed record TunerEntry(string? Key, string? Url);
