using System.Collections.Generic;

namespace LiveTvAccess.Core;

/// <summary>One stream URL listed by more than one tuner.</summary>
/// <param name="SourceId">The media source id (md5 of the URL); internal, for looking up the host.</param>
/// <param name="SourceHash">Short hash shown to admins.</param>
/// <param name="Tuners">The tuners listing it.</param>
/// <param name="ChannelCount">How many channels use it.</param>
public sealed record DuplicateStreamUrl(string SourceId, string SourceHash, IReadOnlyList<string> Tuners, int ChannelCount);
