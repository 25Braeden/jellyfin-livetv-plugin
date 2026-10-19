using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveTvAccess.Core;

/// <summary>
/// Which tuner each user is assigned to. Users not in the map have no tuner.
/// </summary>
public sealed class UserTunerMap
{
    private readonly Dictionary<Guid, string> _userToTuner;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserTunerMap"/> class.
    /// </summary>
    /// <param name="tunerKeys">All known tuner keys.</param>
    /// <param name="assignments">User id to tuner key. Every tuner key must be in <paramref name="tunerKeys"/>.</param>
    public UserTunerMap(IEnumerable<string> tunerKeys, IEnumerable<KeyValuePair<Guid, string>> assignments)
    {
        ArgumentNullException.ThrowIfNull(tunerKeys);
        ArgumentNullException.ThrowIfNull(assignments);

        TunerKeys = tunerKeys.Select(TunerTags.NormalizeKey).Distinct(StringComparer.Ordinal).ToArray();
        _userToTuner = new Dictionary<Guid, string>();
        foreach (var (userId, tunerKey) in assignments)
        {
            var key = TunerTags.NormalizeKey(tunerKey);
            if (!TunerKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new ArgumentException($"User {userId} is assigned to unknown tuner '{tunerKey}'.", nameof(assignments));
            }

            if (!_userToTuner.TryAdd(userId, key))
            {
                throw new ArgumentException($"User {userId} is assigned more than once.", nameof(assignments));
            }
        }
    }

    /// <summary>
    /// Gets all known tuner keys (normalized).
    /// </summary>
    public IReadOnlyList<string> TunerKeys { get; }

    /// <summary>
    /// Gets the number of users with a tuner.
    /// </summary>
    public int AssignedUserCount => _userToTuner.Count;

    /// <summary>
    /// Gets the tuner key assigned to a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The tuner key, or <c>null</c> if the user has no tuner.</returns>
    public string? GetTuner(Guid userId)
    {
        return _userToTuner.TryGetValue(userId, out var key) ? key : null;
    }

    /// <summary>
    /// Gets the channel tags a user must not see: every tuner's tag except their own, plus
    /// <see cref="TunerTags.Unassigned"/>. A user with no tuner gets every tuner's tag (deny by default).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The blocked tags.</returns>
    public IReadOnlyList<string> GetBlockedTags(Guid userId)
    {
        var own = GetTuner(userId);
        return TunerKeys.Where(k => k != own).Select(TunerTags.ForTuner).Append(TunerTags.Unassigned).ToArray();
    }

    /// <summary>
    /// Merges managed blocked tags into a user's existing blocked tags, keeping tags this plugin doesn't manage.
    /// </summary>
    /// <param name="existing">The user's current blocked tags.</param>
    /// <param name="managed">The managed tags to apply (from <see cref="GetBlockedTags"/>), or empty to remove ours.</param>
    /// <returns>The new blocked tag list.</returns>
    public static IReadOnlyList<string> MergeTags(IEnumerable<string> existing, IEnumerable<string> managed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(managed);
        return existing.Where(t => !TunerTags.IsManaged(t)).Concat(managed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Builds a map from configured entries, skipping (with a warning) any entry that is null, has no user id,
    /// names an invalid or unknown tuner, or conflicts with another entry for the same user. A bad entry never
    /// affects other users.
    /// </summary>
    /// <param name="tunerKeys">All known tuner keys.</param>
    /// <param name="entries">The configured entries (may contain nulls).</param>
    /// <param name="warn">Receives one message per skipped or merged entry.</param>
    /// <returns>The map.</returns>
    public static UserTunerMap FromEntries(IEnumerable<string> tunerKeys, IEnumerable<UserTunerEntry?> entries, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(tunerKeys);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(warn);

        var keys = tunerKeys.Select(TunerTags.NormalizeKey).Distinct(StringComparer.Ordinal).ToArray();
        var byUser = new Dictionary<Guid, List<(string Key, string Label)>>();
        var index = 0;
        foreach (var entry in entries)
        {
            index++;
            if (entry is null)
            {
                warn($"user entry #{index} is empty; skipped");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(entry.Label) ? $"#{index}" : $"'{entry.Label}'";
            if (entry.UserId == Guid.Empty)
            {
                warn($"user entry {label} has no user id (unknown user, or saved before ids were resolved); skipped");
                continue;
            }

            string key;
            try
            {
                key = TunerTags.NormalizeKey(entry.TunerKey!);
            }
            catch (ArgumentException)
            {
                warn($"user entry {label} has an invalid tuner key '{entry.TunerKey}'; skipped");
                continue;
            }

            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                warn($"user entry {label} names unknown tuner '{key}'; skipped");
                continue;
            }

            if (!byUser.TryGetValue(entry.UserId, out var list))
            {
                byUser[entry.UserId] = list = new List<(string Key, string Label)>();
            }

            list.Add((key, label));
        }

        var assignments = new List<KeyValuePair<Guid, string>>();
        foreach (var (userId, list) in byUser)
        {
            var distinct = list.Select(e => e.Key).Distinct(StringComparer.Ordinal).ToArray();
            var labels = string.Join(", ", list.Select(e => e.Label));
            if (distinct.Length > 1)
            {
                warn($"user {userId} ({labels}) is assigned to several tuners ({string.Join(", ", distinct)}); no tuner granted");
                continue;
            }

            if (list.Count > 1)
            {
                warn($"user {userId} ({labels}) is listed {list.Count} times for '{distinct[0]}'; using it once");
            }

            assignments.Add(new KeyValuePair<Guid, string>(userId, distinct[0]));
        }

        return new UserTunerMap(keys, assignments);
    }
}
