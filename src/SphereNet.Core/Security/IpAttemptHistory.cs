using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

/// <summary>
/// Per-IP connection attempt history: when the last two attempts happened and how
/// many there have been. Feeds the <c>connectreq_ex</c> / <c>connection_acquired</c>
/// script hooks.
///
/// Bounded on purpose. The dictionary this replaces only ever grew: an entry was
/// created for every distinct source address and nothing removed it, expired it or
/// capped it, so a long-running shard accumulated one record per IP it had ever
/// seen.
///
/// Lifetime follows Source-X's IPHistoryManager (CIPHistoryManager.cpp): an entry
/// has a TTL refreshed by activity, and the attempt count does not decay - it is
/// "forgotten when the IP is forgotten". Pruning runs from the write path so it
/// cannot be left unwired, which is exactly how ConnectionRateLimiter.Cleanup
/// ended up dead code.
/// </summary>
public sealed class IpAttemptHistory
{
    /// <param name="PreviousMs">Tick of the attempt before <paramref name="CurrentMs"/>.</param>
    /// <param name="CurrentMs">Tick of the most recent attempt; also the TTL anchor.</param>
    /// <param name="Count">Attempts since this entry was created.</param>
    public readonly record struct Attempt(long PreviousMs, long CurrentMs, int Count);

    private readonly ConcurrentDictionary<string, Attempt> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly long _ttlMs;
    private readonly int _maxEntries;
    private readonly Func<long> _clock;

    private long _lastPruneMs;

    /// <param name="ttlSeconds">Source-X NETTTL: how long an idle IP is remembered.</param>
    /// <param name="maxEntries">Hard ceiling. Reached only under a distributed flood,
    /// where forgetting the oldest records is better than growing without bound.</param>
    public IpAttemptHistory(int ttlSeconds = 300, int maxEntries = 65536, Func<long>? clock = null)
    {
        _ttlMs = Math.Max(1, ttlSeconds) * 1000L;
        _maxEntries = Math.Max(16, maxEntries);
        _clock = clock ?? (() => Environment.TickCount64);
        _lastPruneMs = _clock();
    }

    public int Count => _entries.Count;

    /// <summary>Record an attempt from <paramref name="ip"/> and return the updated
    /// entry.</summary>
    public Attempt Register(string ip)
    {
        long now = _clock();
        Prune(now);

        return _entries.AddOrUpdate(ip,
            _ => new Attempt(now, now, 1),
            (_, old) => new Attempt(old.CurrentMs, now, old.Count + 1));
    }

    /// <summary>The entry for <paramref name="ip"/>, or a default one when the IP is
    /// unknown or has been forgotten.</summary>
    public Attempt Get(string ip) =>
        _entries.TryGetValue(ip, out var attempt) ? attempt : default;

    public void Forget(string ip) => _entries.TryRemove(ip, out _);

    public void Clear() => _entries.Clear();

    /// <summary>Drop entries whose TTL has lapsed. Rate-limited to once a second so
    /// the accept path stays cheap; Source-X decays its IP history on the same
    /// cadence.</summary>
    private void Prune(long now)
    {
        if (now - Volatile.Read(ref _lastPruneMs) < 1000 && _entries.Count < _maxEntries)
            return;
        Volatile.Write(ref _lastPruneMs, now);

        foreach (var pair in _entries)
        {
            if (now - pair.Value.CurrentMs >= _ttlMs)
                _entries.TryRemove(pair.Key, out _);
        }

        // Still over the ceiling after expiry (a flood from many live addresses):
        // drop the least recently seen until it fits. Pruning runs before the
        // caller's insert, so leave a slot free for it — that keeps the ceiling a
        // real bound rather than one the next write always steps over.
        int excess = _entries.Count - (_maxEntries - 1);
        if (excess <= 0) return;

        foreach (var pair in _entries.OrderBy(static p => p.Value.CurrentMs).Take(excess))
            _entries.TryRemove(pair.Key, out _);
    }
}
