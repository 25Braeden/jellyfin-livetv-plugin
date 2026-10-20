using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>A stream URL listed by more than one tuner, redacted for display.</summary>
/// <param name="Host">The stream's scheme and host only, or <c>(unknown)</c>.</param>
/// <param name="Hash">Short hash identifying the URL.</param>
/// <param name="Tuners">The tuners listing it.</param>
/// <param name="ChannelCount">How many channels use it.</param>
public sealed record DuplicateStreamInfo(string Host, string Hash, IReadOnlyList<string> Tuners, int ChannelCount);
