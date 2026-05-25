using System.Collections.Concurrent;

namespace BrainX.Server.Hubs;

/// <summary>
/// Per-(connection, method) sliding-window rate limiter. Cheap, lock-free,
/// purpose-built for BrainHub — not a general-purpose library. Single
/// process; resets on server restart (acceptable: nonce replay is the real
/// defense, this just keeps spammy peers from saturating the hub).
///
/// Default budgets are tuned for legitimate user behavior:
///   - RegisterBrain: 5 attempts / 60 s — humans don't reconnect that often,
///     and excessive auth failures already get logged.
///   - SetScope:      10 calls / 60 s — UI doesn't change perms that fast.
///   - RequestShare:  20 calls / 60 s — power-users browsing peer notes.
///   - SendShareContent: 60 / 60 s — owner may ship many notes per browse.
/// </summary>
public static class RateLimiter
{
    private static readonly ConcurrentDictionary<(string Conn, string Method), Bucket> Buckets = new();

    private sealed class Bucket
    {
        public readonly Queue<DateTime> Hits = new();
        public readonly object Gate = new();
    }

    /// <summary>
    /// Charge one event against <paramref name="connectionId"/>+<paramref name="method"/>.
    /// Returns true if the event fits within the budget; false if the caller
    /// is over and should be told to back off.
    /// </summary>
    public static bool TryConsume(string connectionId, string method, int limit, TimeSpan window)
    {
        var bucket = Buckets.GetOrAdd((connectionId, method), _ => new Bucket());
        var now = DateTime.UtcNow;
        var cutoff = now - window;
        lock (bucket.Gate)
        {
            // Drop expired hits.
            while (bucket.Hits.Count > 0 && bucket.Hits.Peek() < cutoff)
                bucket.Hits.Dequeue();
            if (bucket.Hits.Count >= limit) return false;
            bucket.Hits.Enqueue(now);
            return true;
        }
    }

    /// <summary>Drop all buckets for a connection. Call from OnDisconnectedAsync
    /// to keep the dict bounded.</summary>
    public static void Forget(string connectionId)
    {
        foreach (var key in Buckets.Keys)
            if (key.Conn == connectionId) Buckets.TryRemove(key, out _);
    }
}
