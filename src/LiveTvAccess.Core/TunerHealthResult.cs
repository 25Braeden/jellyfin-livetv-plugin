using System.Collections.Generic;

namespace LiveTvAccess.Core;

/// <summary>Result of <see cref="TunerHealth.Evaluate"/>.</summary>
/// <param name="Tuners">Per-tuner status.</param>
/// <param name="Problems">Real drift or a bad config: the plugin enters its failed state.</param>
/// <param name="Warnings">Worth fixing, but not a failure.</param>
/// <param name="Duplicates">Stream URLs listed by more than one tuner (a warning).</param>
public sealed record TunerHealthResult(
    IReadOnlyList<TunerStatus> Tuners,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DuplicateStreamUrl> Duplicates);
