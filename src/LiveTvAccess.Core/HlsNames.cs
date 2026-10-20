using System;
using System.Collections.Generic;

namespace LiveTvAccess.Core;

/// <summary>
/// Exact-match rules for the legacy HLS file routes (Jellyfin.Api/Controllers/HlsSegmentController.cs), which serve
/// files from the transcode folder by name. Jellyfin names a job's files from its playlist name N
/// (Jellyfin.Api/Controllers/DynamicHlsController.cs:1612-1615, Jellyfin.Api/Helpers/HlsHelpers.cs:96):
/// playlist <c>N.m3u8</c>, segments <c>N{index}{ext}</c>, fMP4 init <c>N-1{ext}</c>.
/// </summary>
public static class HlsNames
{
    /// <summary>Segment extensions a legacy route may serve (no playlists, logs or other files).</summary>
    public static readonly IReadOnlySet<string> SegmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ts", "mp4", "m4s", "aac", "mp3",
    };

    /// <summary>
    /// Gets whether a route value is a plain file-name stem: 1-128 of a-z, A-Z, 0-9, '-', '_'. Rejects path separators,
    /// dots (so "." and ".."), percent-encoding, whitespace and anything else.
    /// </summary>
    /// <param name="value">The route value (already URL-decoded by ASP.NET Core).</param>
    /// <returns><c>true</c> if safe.</returns>
    public static bool IsSafeName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether <paramref name="segmentId"/> is exactly one of job <paramref name="jobName"/>'s segment names:
    /// the job name followed by 1-9 digits, or the fMP4 init name (job name + "-1").
    /// </summary>
    /// <param name="jobName">The job's playlist name (without ".m3u8").</param>
    /// <param name="segmentId">The requested segment name (without extension).</param>
    /// <returns><c>true</c> on an exact match.</returns>
    public static bool IsSegmentOf(string jobName, string segmentId)
    {
        ArgumentNullException.ThrowIfNull(jobName);
        ArgumentNullException.ThrowIfNull(segmentId);

        // Case-sensitive: ffmpeg writes these names exactly (an md5 in lower case).
        if (!segmentId.StartsWith(jobName, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = segmentId.AsSpan(jobName.Length);
        if (rest.SequenceEqual("-1"))
        {
            return true;
        }

        if (rest.Length is < 1 or > 9)
        {
            return false;
        }

        foreach (var c in rest)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the job names a segment name could belong to (for the legacy audio route, which has no playlist id):
    /// the segment name with 1-9 trailing digits removed, or with a trailing "-1" removed. Each candidate is then
    /// looked up as an exact job path; nothing is matched by prefix.
    /// </summary>
    /// <param name="segmentId">The requested segment name (without extension).</param>
    /// <returns>Candidate job names, longest first.</returns>
    public static IReadOnlyList<string> CandidateJobNames(string segmentId)
    {
        ArgumentNullException.ThrowIfNull(segmentId);

        var candidates = new List<string>();
        if (segmentId.EndsWith("-1", StringComparison.Ordinal) && segmentId.Length > 2)
        {
            candidates.Add(segmentId[..^2]);
            return candidates;
        }

        for (var strip = 1; strip <= 9 && strip < segmentId.Length && char.IsAsciiDigit(segmentId[^strip]); strip++)
        {
            candidates.Add(segmentId[..^strip]);
        }

        return candidates;
    }
}
