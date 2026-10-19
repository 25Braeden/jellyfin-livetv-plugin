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
    /// Gets the tuner key assigned to a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The tuner key, or <c>null</c> if the user has no tuner.</returns>
    public string? GetTuner(Guid userId)
    {
        return _userToTuner.TryGetValue(userId, out var key) ? key : null;
    }

    /// <summary>
    /// Gets the channel tags a user must not see: every tuner's tag except their own.
    /// A user with no tuner gets every tuner's tag (deny by default).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The blocked tags.</returns>
    public IReadOnlyList<string> GetBlockedTags(Guid userId)
    {
        var own = GetTuner(userId);
        return TunerKeys.Where(k => k != own).Select(TunerTags.ForTuner).ToArray();
    }
}
