using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Per-key locks that serialize Live TV stream opening (final review Q4). The "is this media source already open, and
/// on which tuner?" check (AccessService.EffectiveTuner) and the open itself must not interleave between two requests:
/// otherwise both pass the check and Jellyfin hands the second the first's stream, on the first's tuner
/// (DefaultLiveTvService.cs:466-476). Keys are taken in a fixed order (no deadlock), waits are bounded, and entries are
/// removed when unused.
/// </summary>
public static class OpeningLocks
{
    private static readonly object _sync = new();
    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets how long a request waits for another open of the same channel or stream.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the number of keys currently held or waited on (for tests).</summary>
    internal static int ActiveKeys
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Acquires every key, in a fixed order, within <see cref="Timeout"/> in total.
    /// </summary>
    /// <param name="keys">The keys (channel and media source).</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>A handle that releases them, or <c>null</c> on timeout (nothing held).</returns>
    public static async Task<IDisposable?> AcquireAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ordered = keys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var held = new Handle();
        var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        try
        {
            foreach (var key in ordered)
            {
                var entry = Rent(key);
                bool acquired;
                try
                {
                    var remaining = TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                    acquired = await entry.Semaphore.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Return(key, entry);
                    throw;
                }

                if (!acquired)
                {
                    Return(key, entry);
                    held.Dispose();
                    return null;
                }

                held.Add(key, entry);
            }

            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    private static Entry Rent(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
            return entry;
        }
    }

    private static void Return(string key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Handle : IDisposable
    {
        private readonly List<(string Key, Entry Entry)> _held = new();
        private int _disposed;

        public void Add(string key, Entry entry) => _held.Add((key, entry));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            for (var i = _held.Count - 1; i >= 0; i--)
            {
                var (key, entry) = _held[i];
                entry.Semaphore.Release();
                Return(key, entry);
            }
        }
    }
}
