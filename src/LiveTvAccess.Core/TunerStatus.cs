namespace LiveTvAccess.Core;

/// <summary>One configured tuner as the self-check sees it. Never holds the URL itself.</summary>
/// <param name="Key">The tuner key.</param>
/// <param name="Location">Redacted location (<see cref="Redact.Location"/>).</param>
/// <param name="UrlHash">Short hash of the URL, to tell tuners apart.</param>
/// <param name="InJellyfin">Whether Jellyfin has an M3U tuner with exactly this URL.</param>
/// <param name="ChannelCount">Channels in the library that belong to it.</param>
/// <param name="UserCount">Users assigned to it.</param>
public sealed record TunerStatus(string Key, string Location, string UrlHash, bool InJellyfin, int ChannelCount, int UserCount);
