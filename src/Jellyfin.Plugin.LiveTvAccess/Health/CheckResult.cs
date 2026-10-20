namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>One self-check item.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">Whether it passed.</param>
/// <param name="Detail">Details (never URLs or credentials).</param>
public sealed record CheckResult(string Name, bool Ok, string Detail);
