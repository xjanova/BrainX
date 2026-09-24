using System.Collections.Concurrent;

namespace BrainX.Server.Cloud;

/// <summary>
/// Sliding-window limiter keyed by an arbitrary string ("login:1.2.3.4",
/// "api:&lt;tokenId&gt;", "xman"). Same shape as the hub's RateLimiter, but
/// clock-injected (so the harness can step time instead of sleeping) and
/// self-cleaning: keys are client IPs and token ids, which arrive from the
/// internet, so idle buckets are swept rather than kept forever.
/// </summary>
public sealed class CloudRateLimiter
{
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _nextSweepTicks;

    private sealed class Bucket
    {
        public readonly Queue<long> Hits = new();
        public long WindowTicks;
        public long LastHitTicks;
    }

    public CloudRateLimiter(TimeProvider clock) => _clock = clock;

    /// <summary>
    /// Charge one event. False = over budget; <paramref name="retryAfter"/> says
    /// when the oldest hit leaves the window.
    /// </summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var cutoff = now - window.Ticks;
        var b = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (b)
        {
            b.WindowTicks = window.Ticks;
            while (b.Hits.Count > 0 && b.Hits.Peek() <= cutoff) b.Hits.Dequeue();
            if (b.Hits.Count >= limit)
            {
                retryAfter = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, b.Hits.Peek() - cutoff));
                return false;
            }
            b.Hits.Enqueue(now);
            b.LastHitTicks = now;
        }
        MaybeSweep(now);
        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>Is the key already over budget? Does not charge.</summary>
    public bool IsLimited(string key, int limit, TimeSpan window)
    {
        if (!_buckets.TryGetValue(key, out var b)) return false;
        var cutoff = _clock.GetUtcNow().UtcTicks - window.Ticks;
        lock (b)
        {
            while (b.Hits.Count > 0 && b.Hits.Peek() <= cutoff) b.Hits.Dequeue();
            return b.Hits.Count >= limit;
        }
    }

    private void MaybeSweep(long now)
    {
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now < next) return;
        if (Interlocked.CompareExchange(ref _nextSweepTicks, now + TimeSpan.TicksPerMinute, next) != next) return;
        foreach (var (key, b) in _buckets)
        {
            bool idle;
            lock (b) idle = now - b.LastHitTicks > b.WindowTicks;
            if (idle) _buckets.TryRemove(key, out _);
        }
    }
}
