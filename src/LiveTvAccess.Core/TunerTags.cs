using System;
using System.Globalization;

namespace LiveTvAccess.Core;

/// <summary>
/// Naming for the per-tuner tags placed on Live TV channels (e.g. "livetv-tuner1").
/// </summary>
public static class TunerTags
{
    /// <summary>
    /// Prefix shared by every tag this plugin manages. Tags without it are never touched.
    /// </summary>
    public const string Prefix = "livetv-";

    /// <summary>
    /// Tag for channels that match no configured tuner. Blocked for every non-admin user (fail closed).
    /// </summary>
    public const string Unassigned = Prefix + "unassigned";

    /// <summary>
    /// Gets the tag for a tuner key.
    /// </summary>
    /// <param name="tunerKey">Short tuner key: lowercase letters, digits and dashes (e.g. "tuner1").</param>
    /// <returns>The tag, e.g. "livetv-tuner1".</returns>
    public static string ForTuner(string tunerKey)
    {
        return Prefix + NormalizeKey(tunerKey);
    }

    /// <summary>
    /// Gets whether a tag is one this plugin manages.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns><c>true</c> if the tag starts with <see cref="Prefix"/> (case-insensitive).</returns>
    public static bool IsManaged(string? tag)
    {
        return tag is not null && tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates and normalizes a tuner key to lowercase.
    /// </summary>
    /// <param name="tunerKey">The tuner key.</param>
    /// <returns>The normalized key.</returns>
    /// <exception cref="ArgumentException">If the key is empty or contains characters other than a-z, 0-9 and '-'.</exception>
    public static string NormalizeKey(string tunerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunerKey);

        var key = tunerKey.Trim().ToLower(CultureInfo.InvariantCulture);
        foreach (var c in key)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException($"Invalid tuner key '{tunerKey}': use a-z, 0-9 and '-'.", nameof(tunerKey));
            }
        }

        return key;
    }
}
